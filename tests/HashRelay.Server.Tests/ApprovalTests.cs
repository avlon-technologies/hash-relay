using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using HashRelay.Server.Services;
using HashRelay.Server.Services.Approvals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>Real time plus a test-controlled offset, so expiry can be crossed without sleeping.</summary>
public sealed class ShiftableClock : TimeProvider
{
    public TimeSpan Offset { get; set; }
    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}

/// <summary>The real routing publisher, or a failure on demand.</summary>
public sealed class SwitchableNotifier(IApprovalNotificationPublisher inner, NotifierSwitch sw) : IApprovalNotificationPublisher
{
    public Task<PublishResult> PublishAsync(Approval approval, CancellationToken ct)
    {
        Interlocked.Increment(ref sw.Calls);
        return sw.Fail ? throw new InvalidOperationException("notification transport unavailable") : inner.PublishAsync(approval, ct);
    }
}

public sealed class NotifierSwitch { public volatile bool Fail; public int Calls; }

/// <summary>
/// Actionable approvals, Milestone 1, against the real server and a real SQLite file: HTTP for every boundary a
/// caller can reach, the services directly only to force races and failures.
/// </summary>
public sealed class ApprovalTests : IAsyncLifetime
{
    private readonly ShiftableClock _clock = new();
    private readonly NotifierSwitch _notifier = new();
    private readonly RelayFactory _factory;
    private RelayFactory.Workspace _ws = null!;      // requester's workspace; its owner is an approver
    private RelayFactory.Workspace _member = null!;  // second member of _ws (its own sign-up workspace is incidental)
    private RelayFactory.Workspace _other = null!;   // an unrelated workspace

    public ApprovalTests()
    {
        _factory = new RelayFactory
        {
            Settings = { ["HashRelay:Approvals:SweepIntervalSeconds"] = "0" },
            TestServices = s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(_clock);
                s.RemoveAll<IApprovalNotificationPublisher>();
                s.AddScoped<IApprovalNotificationPublisher>(sp =>
                    new SwitchableNotifier(new RoutingApprovalNotificationPublisher(sp.GetRequiredService<RoutingService>()), _notifier));
            },
        };
    }

    public async ValueTask InitializeAsync()
    {
        _ws = await _factory.CreateWorkspaceAsync("Requesters");
        _member = await _factory.CreateWorkspaceAsync("Member home");
        _other = await _factory.CreateWorkspaceAsync("Elsewhere");
        await AddMemberAsync(_ws.TenantId, _member.AccountId, Roles.Member);
    }

    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    // ------------------------------------------------------------------ helpers

    private async Task AddMemberAsync(Guid tenantId, Guid accountId, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        db.Memberships.Add(new Membership { TenantId = tenantId, AccountId = accountId, Role = role, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private T InScope<T>(Func<RelayDbContext, T> f)
    {
        using var scope = _factory.Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<RelayDbContext>());
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = _factory.Services.CreateScope();
        return await f(scope.ServiceProvider);
    }

    private static object Body(IEnumerable<Guid> approvers, DateTimeOffset? expires = null, string subject = "Deploy build 812 to production",
        string[]? tags = null, string? correlation = "chg-812", string? description = "Release 812: 14 commits, migrations none.")
        => new { subject, targetUserIds = approvers, expiresAt = expires ?? DateTimeOffset.UtcNow.AddHours(1), description, tags, requesterCorrelationId = correlation };

    private static async Task<HttpResponseMessage> PostCreateAsync(HttpClient client, object body, string key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/approvals") { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(req);
    }

    private async Task<ApprovalResource> CreateAsync(IEnumerable<Guid>? approvers = null, DateTimeOffset? expires = null, string[]? tags = null, string? apiKey = null)
    {
        var res = await PostCreateAsync(_factory.ApiClient(apiKey ?? _ws.ApiKey), Body(approvers ?? [_ws.AccountId, _member.AccountId], expires, tags: tags),
            Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await res.ReadJson<ApprovalResource>();
    }

    private static string Token(string html)
        => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>Signs in as <paramref name="who"/>, opens the decision page and posts to the JSON decision endpoint with its antiforgery token.</summary>
    private async Task<HttpResponseMessage> DecideAsync(RelayFactory.Workspace who, Guid id, string decision, string? key = null, HttpClient? browser = null)
    {
        browser ??= await _factory.LoginAsync(who);
        var page = await browser.GetAsync($"/approvals/{id}");
        var token = page.IsSuccessStatusCode ? Token(await page.Content.ReadAsStringAsync()) : "";
        if (token == "") token = Token(await browser.GetStringAsync("/dashboard")); // not an approver: still send a valid token
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/approvals/{id}/decision") { Content = JsonContent.Create(new { decision, note = "ok" }) };
        req.Headers.Add("RequestVerificationToken", token);
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return await browser.SendAsync(req);
    }

    private async Task<ApprovalResource> GetAsync(Guid id, string? apiKey = null)
    {
        var res = await _factory.ApiClient(apiKey ?? _ws.ApiKey).GetAsync($"/api/v1/approvals/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.ReadJson<ApprovalResource>();
    }

    private async Task<(ServiceAccount Account, string Key, string Key2)> ServiceAccountAsync(string[] scopes, string tags = "ops-*")
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ServiceAccountService>();
        var account = await svc.CreateAsync(_ws.TenantId, _ws.AccountId, ServiceAccountService.Validate("deploy-bot", null, scopes, tags, null, 60), CancellationToken.None);
        var (_, key) = await svc.IssueKeyAsync(_ws.TenantId, account.Id, _ws.AccountId, null, null, CancellationToken.None);
        var (_, key2) = await svc.IssueKeyAsync(_ws.TenantId, account.Id, _ws.AccountId, "second", null, CancellationToken.None);
        return (account, key, key2);
    }

    private List<ApprovalAudit> Audit(Guid id) => InScope(db => db.ApprovalAudits.AsNoTracking().Where(a => a.ApprovalId == id).OrderBy(a => a.Version).ThenBy(a => a.At).ToList());
    private List<ApprovalOutbox> Outbox(Guid id) => InScope(db => db.ApprovalOutbox.AsNoTracking().Where(o => o.ApprovalId == id).OrderBy(o => o.Version).ToList());
    private Approval Row(Guid id) => InScope(db => db.Approvals.AsNoTracking().First(a => a.Id == id));

    private static async Task<ApiError> ErrorOf(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<ApiError>())!;

    // ------------------------------------------------------------------ 1. create

    [Fact]
    public async Task Create_returns_pending_owned_by_the_authenticated_requester()
    {
        // Body fields naming a workspace or requester are not part of the contract and must not be honoured.
        var res = await PostCreateAsync(_factory.ApiClient(_ws.ApiKey), new
        {
            subject = "Rotate prod DB password", targetUserIds = new[] { _member.AccountId }, expiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            workspaceId = _other.TenantId, requesterId = Guid.NewGuid(), requester = new { id = Guid.NewGuid(), name = "spoof" },
        }, "create-1");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var a = await res.ReadJson<ApprovalResource>();
        Assert.Equal(ApprovalStatuses.Pending, a.Status);
        Assert.False(a.Proceed);
        Assert.Equal(1, a.Version);
        Assert.Null(a.Resolution);
        Assert.Equal($"/api/v1/approvals/{a.Id}", res.Headers.Location!.OriginalString);
        Assert.Equal($"http://localhost/approvals/{a.Id}", a.DetailUrl);
        Assert.NotEqual("spoof", a.Requester.Name);

        var row = Row(a.Id);
        Assert.Equal(_ws.TenantId, row.TenantId);
        var keyId = InScope(db => db.Credentials.Where(c => c.TenantId == _ws.TenantId && c.Label == "full").Select(c => c.Id).First());
        Assert.Equal(keyId, row.RequesterCredentialId);
        Assert.Equal(keyId, a.Requester.Id);
    }

    [Fact]
    public async Task Create_validates_input_and_requires_an_idempotency_key()
    {
        var client = _factory.ApiClient(_ws.ApiKey);
        var noKey = await client.PostAsJsonAsync("/api/v1/approvals", Body([_member.AccountId]));
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(ErrorCodes.Validation, (await ErrorOf(noKey)).Code);

        var past = await PostCreateAsync(client, Body([_member.AccountId], DateTimeOffset.UtcNow.AddSeconds(5)), "v-1");
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.Contains("expiresAt", (await ErrorOf(past)).Details!.Keys);

        var none = await PostCreateAsync(client, Body([]), "v-2");
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("targetUserIds", (await ErrorOf(none)).Details!.Keys);
    }

    [Fact]
    public async Task Only_application_credentials_with_the_right_scope_may_request()
    {
        // Subscribe-only API key: no publish scope.
        var sub = await PostCreateAsync(_factory.ApiClient(_ws.SubscribeOnlyKey), Body([_member.AccountId]), "s-1");
        Assert.Equal(HttpStatusCode.Forbidden, sub.StatusCode);

        // A paired device token, even with publish scope, is a person's receiver and cannot request.
        var device = await InScopeAsync(async sp =>
        {
            var (_, secret) = await sp.GetRequiredService<CredentialService>().IssueDeviceTokenAsync(_ws.TenantId, _ws.AccountId, Guid.NewGuid(), "laptop",
                [Scopes.Publish, Scopes.Subscribe], CancellationToken.None);
            return secret;
        });
        var dev = await PostCreateAsync(_factory.ApiClient(device), Body([_member.AccountId]), "s-2");
        Assert.Equal(HttpStatusCode.Forbidden, dev.StatusCode);

        // A service key without approvals:create.
        var (_, publishOnly, _) = await ServiceAccountAsync([ServiceScopes.Publish]);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostCreateAsync(_factory.ApiClient(publishOnly), Body([_member.AccountId]), "s-3")).StatusCode);

        // Unauthenticated.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostCreateAsync(_factory.ApiClient(null), Body([_member.AccountId]), "s-4")).StatusCode);
    }

    [Fact]
    public async Task Service_account_requests_within_its_tag_allowlist_and_any_of_its_keys_reads_the_outcome()
    {
        var (_, key, key2) = await ServiceAccountAsync([ServiceScopes.ApprovalsCreate]);
        var denied = await PostCreateAsync(_factory.ApiClient(key), Body([_member.AccountId], tags: ["finance"]), "sa-1");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var ok = await PostCreateAsync(_factory.ApiClient(key), Body([_member.AccountId], tags: ["ops-deploys"]), "sa-2");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var a = await ok.ReadJson<ApprovalResource>();
        Assert.Equal("deploy-bot", a.Requester.Name);
        Assert.Equal(ApprovalStatuses.Pending, (await GetAsync(a.Id, key2)).Status);
        // The service key cannot read through publish-only routes it lacks, nor decide (covered below).
    }

    // ------------------------------------------------------------------ 2 and 3. idempotent create

    [Fact]
    public async Task Repeated_create_with_the_same_key_and_payload_returns_the_original()
    {
        var client = _factory.ApiClient(_ws.ApiKey);
        var expires = DateTimeOffset.UtcNow.AddHours(2);
        var first = await PostCreateAsync(client, Body([_member.AccountId, _ws.AccountId], expires), "same-key");
        // Same approvers in another order, same expiry: the same request.
        var again = await PostCreateAsync(client, Body([_ws.AccountId, _member.AccountId], expires), "same-key");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((await first.ReadJson<ApprovalResource>()).Id, (await again.ReadJson<ApprovalResource>()).Id);
        Assert.Equal(1, InScope(db => db.Approvals.Count(a => a.TenantId == _ws.TenantId && a.IdempotencyKey == "same-key")));

        // Concurrent retries of one request also collapse to one approval.
        var key = "burst-" + Guid.NewGuid().ToString("N");
        var burst = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostCreateAsync(_factory.ApiClient(_ws.ApiKey), Body([_member.AccountId], expires), key)));
        Assert.All(burst, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        Assert.Single(burst, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(1, InScope(db => db.Approvals.Count(a => a.IdempotencyKey == key)));
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_with_a_different_payload_is_rejected()
    {
        var client = _factory.ApiClient(_ws.ApiKey);
        var expires = DateTimeOffset.UtcNow.AddHours(2);
        Assert.Equal(HttpStatusCode.Created, (await PostCreateAsync(client, Body([_member.AccountId], expires), "k-conflict")).StatusCode);
        var changed = await PostCreateAsync(client, Body([_member.AccountId], expires, subject: "Deploy build 813 to production"), "k-conflict");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        var err = await ErrorOf(changed);
        Assert.Equal(ErrorCodes.Conflict, err.Code);
        Assert.False(string.IsNullOrEmpty(err.TraceId));
        Assert.Equal(1, InScope(db => db.Approvals.Count(a => a.IdempotencyKey == "k-conflict")));

        // The key is scoped to the credential: another key may use the same value for its own request.
        var other = await PostCreateAsync(_factory.ApiClient(_other.ApiKey), Body([_other.AccountId], expires), "k-conflict");
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    // ------------------------------------------------------------------ 4 and 11. decide, then retrieve

    [Fact]
    public async Task Designated_approver_approves_and_requester_reads_the_committed_outcome()
    {
        var a = await CreateAsync();
        var res = await DecideAsync(_member, a.Id, ApprovalDecisions.Approve);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var decided = await res.ReadJson<ApprovalResource>();
        Assert.Equal(ApprovalStatuses.Approved, decided.Status);

        var read = await GetAsync(a.Id);
        Assert.Equal(ApprovalStatuses.Approved, read.Status);
        Assert.True(read.Proceed);
        Assert.Equal(2, read.Version);
        Assert.Equal(ApprovalCauses.Decision, read.Resolution!.Cause);
        Assert.Equal(_member.AccountId, read.Resolution.DecidedBy);
        Assert.Equal("Tester", read.Resolution.DecidedByName);
        Assert.Equal("ok", read.Resolution.Note);
        Assert.Equal("chg-812", read.RequesterCorrelationId);
    }

    [Fact]
    public async Task Designated_approver_rejects_and_proceed_is_false()
    {
        var a = await CreateAsync();
        var res = await DecideAsync(_ws, a.Id, ApprovalDecisions.Reject);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var read = await GetAsync(a.Id);
        Assert.Equal(ApprovalStatuses.Rejected, read.Status);
        Assert.False(read.Proceed);
        Assert.Equal(_ws.AccountId, read.Resolution!.DecidedBy);

        // Terminal is immutable: a later approval attempt by the other approver conflicts and changes nothing.
        var late = await DecideAsync(_member, a.Id, ApprovalDecisions.Approve);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal([ApprovalStatuses.Rejected], (await ErrorOf(late)).Details!["status"]);
        Assert.Equal(ApprovalStatuses.Rejected, (await GetAsync(a.Id)).Status);
    }

    [Fact]
    public async Task The_decision_page_form_records_a_decision()
    {
        var a = await CreateAsync();
        var browser = await _factory.LoginAsync(_member);
        var html = await browser.GetStringAsync($"/approvals/{a.Id}");
        Assert.Contains("Deploy build 812 to production", html);
        Assert.Contains("Release 812: 14 commits", html);
        var key = Regex.Match(html, "name=\"Key\" value=\"([^\"]+)\"").Groups[1].Value;
        var post = await browser.PostAsync($"/approvals/{a.Id}?handler=Approve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html), ["Key"] = key, ["Note"] = "looks good",
        }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Contains("You approved this request.", await post.Content.ReadAsStringAsync());
        Assert.Equal(ApprovalStatuses.Approved, (await GetAsync(a.Id)).Status);

        // The pending list no longer shows it.
        var other = await CreateAsync();
        var list = await browser.GetStringAsync("/approvals");
        Assert.Contains($"/approvals/{other.Id}", list);
        Assert.DoesNotContain($"/approvals/{a.Id}\"", list);
    }

    // ------------------------------------------------------------------ 5. unauthorized approvers

    [Fact]
    public async Task Only_a_signed_in_designated_member_can_decide()
    {
        var a = await CreateAsync(approvers: [_ws.AccountId]);

        // A member of the workspace who is not designated.
        var notDesignated = await DecideAsync(_member, a.Id, ApprovalDecisions.Approve);
        Assert.Equal(HttpStatusCode.Forbidden, notDesignated.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, (await ErrorOf(notDesignated)).Code);
        var page = await (await _factory.LoginAsync(_member)).GetAsync($"/approvals/{a.Id}");
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);

        // Machine credentials never confer approval authority: API key with every scope, service key, device token.
        var (_, svcKey, _) = await ServiceAccountAsync([ServiceScopes.ApprovalsCreate, ServiceScopes.Publish, ServiceScopes.Read]);
        foreach (var key in new[] { _ws.ApiKey, svcKey })
        {
            var res = await _factory.ApiClient(key).PostAsJsonAsync($"/api/v1/approvals/{a.Id}/decision", new { decision = "approve" });
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.Equal(ErrorCodes.Unauthenticated, (await ErrorOf(res)).Code);
        }

        // A signed-in designated approver without an antiforgery token.
        var browser = await _factory.LoginAsync(_ws);
        var noToken = await browser.PostAsJsonAsync($"/api/v1/approvals/{a.Id}/decision", new { decision = "approve" });
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);

        // An invalid decision value.
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(_ws, a.Id, "maybe", browser: browser)).StatusCode);

        Assert.Equal(ApprovalStatuses.Pending, (await GetAsync(a.Id)).Status);
        Assert.DoesNotContain(Audit(a.Id), x => x.Action is ApprovalAuditActions.Approved or ApprovalAuditActions.Rejected);
    }

    [Fact]
    public async Task An_approver_removed_from_the_workspace_can_no_longer_decide()
    {
        var a = await CreateAsync();
        var browser = await _factory.LoginAsync(_member);
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<RelayDbContext>();
            return await db.Memberships.Where(m => m.TenantId == _ws.TenantId && m.AccountId == _member.AccountId).ExecuteDeleteAsync();
        });
        var res = await DecideAsync(_member, a.Id, ApprovalDecisions.Approve, browser: browser);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(ApprovalStatuses.Pending, (await GetAsync(a.Id)).Status);
        await AddMemberAsync(_ws.TenantId, _member.AccountId, Roles.Member);
    }

    // ------------------------------------------------------------------ 6. tenant isolation

    [Fact]
    public async Task Approvals_are_isolated_by_workspace()
    {
        var a = await CreateAsync();
        var foreign = _factory.ApiClient(_other.ApiKey);
        var get = await foreign.GetAsync($"/api/v1/approvals/{a.Id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, (await ErrorOf(get)).Code);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsync($"/api/v1/approvals/{a.Id}/cancel", null)).StatusCode);

        // Another workspace's person can neither see nor decide it.
        Assert.Equal(HttpStatusCode.NotFound, (await (await _factory.LoginAsync(_other)).GetAsync($"/approvals/{a.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(_other, a.Id, ApprovalDecisions.Approve)).StatusCode);

        // Nor can a requester designate someone outside its workspace; unknown and foreign ids get the same answer.
        var foreignApprover = await PostCreateAsync(_factory.ApiClient(_ws.ApiKey), Body([_other.AccountId]), "iso-1");
        var unknownApprover = await PostCreateAsync(_factory.ApiClient(_ws.ApiKey), Body([Guid.NewGuid()]), "iso-2");
        Assert.Equal(HttpStatusCode.BadRequest, foreignApprover.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownApprover.StatusCode);
        Assert.Equal(Regex.Replace((await ErrorOf(foreignApprover)).Message, "[0-9a-f-]{36}", "ID"),
            Regex.Replace((await ErrorOf(unknownApprover)).Message, "[0-9a-f-]{36}", "ID"));

        // A second key in the same workspace is a different requester: it cannot read what the first created.
        var secondKey = await InScopeAsync(async sp => (await sp.GetRequiredService<CredentialService>()
            .IssueApiKeyAsync(_ws.TenantId, _ws.AccountId, "other-integration", [Scopes.Publish], null, CancellationToken.None)).Secret);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ApiClient(secondKey).GetAsync($"/api/v1/approvals/{a.Id}")).StatusCode);
    }

    // ------------------------------------------------------------------ 7. concurrency

    [Fact]
    public async Task Concurrent_decisions_produce_exactly_one_winner()
    {
        for (var round = 0; round < 5; round++)
        {
            var a = await CreateAsync();
            async Task<string> Decide(Guid account, string decision)
            {
                using var scope = _factory.Services.CreateScope();
                try
                {
                    return (await scope.ServiceProvider.GetRequiredService<ApprovalService>()
                        .DecideAsync(account, a.Id, new ApprovalDecisionRequest(decision), null, null, CancellationToken.None)).Status;
                }
                catch (ApiException ex) when (ex.Status == 409) { return "conflict"; }
            }
            var results = await Task.WhenAll(
                Decide(_ws.AccountId, ApprovalDecisions.Approve), Decide(_member.AccountId, ApprovalDecisions.Reject),
                Decide(_ws.AccountId, ApprovalDecisions.Reject), Decide(_member.AccountId, ApprovalDecisions.Approve));

            var winners = results.Where(r => r != "conflict").ToList();
            Assert.Single(winners);
            var final = await GetAsync(a.Id);
            Assert.Equal(winners[0], final.Status);
            Assert.Equal(2, final.Version);
            Assert.Single(Audit(a.Id), x => x.Action is ApprovalAuditActions.Approved or ApprovalAuditActions.Rejected);
            Assert.Single(Outbox(a.Id), o => o.Kind == ApprovalOutboxKinds.Resolved);
        }
    }

    // ------------------------------------------------------------------ 8. expiry

    [Fact]
    public async Task An_expired_request_cannot_be_approved()
    {
        var a = await CreateAsync(expires: DateTimeOffset.UtcNow.AddMinutes(2));
        var browser = await _factory.LoginAsync(_member);
        _clock.Offset = TimeSpan.FromMinutes(3);
        try
        {
            var res = await DecideAsync(_member, a.Id, ApprovalDecisions.Approve, browser: browser);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal([ApprovalStatuses.Expired], (await ErrorOf(res)).Details!["status"]);

            var read = await GetAsync(a.Id);
            Assert.Equal(ApprovalStatuses.Expired, read.Status);
            Assert.False(read.Proceed);
            Assert.Equal(ApprovalCauses.Expiry, read.Resolution!.Cause);
            Assert.Null(read.Resolution.DecidedBy); // expiry is not a person's decision

            var evt = JsonSerializer.Deserialize<ApprovalOutcome>(Outbox(a.Id).Single(o => o.Kind == ApprovalOutboxKinds.Resolved).Payload!, ApprovalService.Json)!;
            Assert.Equal((ApprovalStatuses.Expired, ApprovalCauses.Expiry, (Guid?)null), (evt.Status, evt.Cause, evt.DecidedBy));
            Assert.Single(Audit(a.Id), x => x.Action == ApprovalAuditActions.Expired && x.ActorKind == ApprovalActorKinds.System);
        }
        finally { _clock.Offset = TimeSpan.Zero; }
    }

    [Fact]
    public async Task The_sweep_expires_due_requests_without_a_reader()
    {
        var a = await CreateAsync(expires: DateTimeOffset.UtcNow.AddMinutes(2));
        _clock.Offset = TimeSpan.FromMinutes(5);
        try
        {
            var n = await InScopeAsync(sp => sp.GetRequiredService<ApprovalService>().ExpireDueAsync(100, CancellationToken.None));
            Assert.True(n >= 1);
            Assert.Equal(ApprovalStatuses.Expired, Row(a.Id).Status);
            Assert.Equal(0, await InScopeAsync(sp => sp.GetRequiredService<ApprovalService>().ExpireDueAsync(100, CancellationToken.None)));
        }
        finally { _clock.Offset = TimeSpan.Zero; }
    }

    // ------------------------------------------------------------------ 9. cancel

    [Fact]
    public async Task Cancellation_prevents_approval()
    {
        var a = await CreateAsync();
        var client = _factory.ApiClient(_ws.ApiKey);
        var cancel = await client.PostAsync($"/api/v1/approvals/{a.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var c = await cancel.ReadJson<ApprovalResource>();
        Assert.Equal(ApprovalStatuses.Cancelled, c.Status);
        Assert.Equal(ApprovalCauses.Cancellation, c.Resolution!.Cause);
        Assert.Null(c.Resolution.DecidedBy);

        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(_member, a.Id, ApprovalDecisions.Approve)).StatusCode);
        Assert.Equal(ApprovalStatuses.Cancelled, (await GetAsync(a.Id)).Status);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/approvals/{a.Id}/cancel", null)).StatusCode); // idempotent

        // Cancelling something already decided is a conflict and leaves the decision in place.
        var b = await CreateAsync();
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(_member, b.Id, ApprovalDecisions.Approve)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/approvals/{b.Id}/cancel", null)).StatusCode);
        Assert.Equal(ApprovalStatuses.Approved, (await GetAsync(b.Id)).Status);
    }

    // ------------------------------------------------------------------ 10. atomic state, audit and outbox

    [Fact]
    public async Task State_audit_and_outbox_commit_together()
    {
        var a = await CreateAsync(tags: ["deploys"]);
        Assert.Equal([ApprovalAuditActions.Created, ApprovalAuditActions.NotificationPublished], Audit(a.Id).Select(x => x.Action));
        Assert.Single(Outbox(a.Id), o => o.Kind == ApprovalOutboxKinds.Notification && o.ProcessedAt is not null);

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(_member, a.Id, ApprovalDecisions.Approve)).StatusCode);
        var approved = Audit(a.Id).Single(x => x.Action == ApprovalAuditActions.Approved);
        Assert.Equal((2, ApprovalActorKinds.Account, (Guid?)_member.AccountId, "chg-812"), (approved.Version, approved.ActorKind, approved.ActorId, approved.CorrelationId));
        var resolved = Outbox(a.Id).Single(o => o.Kind == ApprovalOutboxKinds.Resolved);
        Assert.Equal(2, resolved.Version);
        var evt = JsonSerializer.Deserialize<ApprovalOutcome>(resolved.Payload!, ApprovalService.Json)!;
        Assert.Equal(new ApprovalOutcome(a.Id, _ws.TenantId, a.Requester.Id, "chg-812", ApprovalStatuses.Approved, ApprovalCauses.Decision, 2,
            Row(a.Id).ResolvedAt!.Value, _member.AccountId), evt with { ResolvedAt = Row(a.Id).ResolvedAt!.Value });

        // The audit is append-only.
        Assert.Throws<InvalidOperationException>(() => InScope(db =>
        {
            var row = db.ApprovalAudits.First(x => x.ApprovalId == a.Id);
            row.Detail = "rewritten";
            return db.SaveChanges();
        }));
    }

    [Fact]
    public async Task A_failed_outbox_write_rolls_back_the_transition()
    {
        var a = await CreateAsync();
        // Occupy the outbox slot the decision will need, so its insert fails inside the transaction.
        InScope(db =>
        {
            db.ApprovalOutbox.Add(new ApprovalOutbox
            {
                Id = Guid.NewGuid(), TenantId = _ws.TenantId, ApprovalId = a.Id, Kind = ApprovalOutboxKinds.Resolved, Version = 2,
                CreatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.MaxValue, ProcessedAt = DateTimeOffset.UtcNow,
            });
            return db.SaveChanges();
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => InScopeAsync(sp => sp.GetRequiredService<ApprovalService>()
            .DecideAsync(_member.AccountId, a.Id, new ApprovalDecisionRequest(ApprovalDecisions.Approve), null, null, CancellationToken.None)));

        var row = Row(a.Id);
        Assert.Equal((ApprovalStatuses.Pending, 1, (Guid?)null), (row.Status, row.Version, row.DecidedByAccountId));
        Assert.DoesNotContain(Audit(a.Id), x => x.Action == ApprovalAuditActions.Approved);
    }

    [Fact]
    public async Task An_approval_survives_notification_failure_and_the_intent_is_retried()
    {
        _notifier.Fail = true;
        ApprovalResource a;
        try
        {
            a = await CreateAsync(tags: ["deploys"]);
            Assert.Equal(ApprovalStatuses.Pending, a.Status);
            Assert.Null(a.NotificationId);
            var intent = Outbox(a.Id).Single(o => o.Kind == ApprovalOutboxKinds.Notification);
            Assert.Null(intent.ProcessedAt);
            Assert.Null(intent.FailedAt);
            Assert.Equal(1, intent.Attempts);
            Assert.Contains("unavailable", intent.LastError);
            Assert.Equal(ApprovalStatuses.Pending, (await GetAsync(a.Id)).Status);
        }
        finally { _notifier.Fail = false; }

        // Past the backoff, the sweep publishes it.
        _clock.Offset = TimeSpan.FromMinutes(1);
        try
        {
            await InScopeAsync(sp => sp.GetRequiredService<ApprovalService>().DispatchDueAsync(100, CancellationToken.None));
        }
        finally { _clock.Offset = TimeSpan.Zero; }
        Assert.NotNull(Outbox(a.Id).Single(o => o.Kind == ApprovalOutboxKinds.Notification).ProcessedAt);
        var read = await GetAsync(a.Id);
        Assert.NotNull(read.NotificationId);
        var n = InScope(db => db.Notifications.AsNoTracking().First(x => x.Id == read.NotificationId));
        Assert.Equal(a.DetailUrl, n.Url);
        Assert.Contains("Deploy build 812 to production", n.Title);
        Assert.DoesNotContain("14 commits", n.Body ?? ""); // the description stays behind authentication
        Assert.Equal(_ws.TenantId, n.TenantId);
    }

    // ------------------------------------------------------------------ 12. redelivery

    [Fact]
    public async Task Repeated_delivery_and_retries_produce_no_repeated_effects()
    {
        var a = await CreateAsync(tags: ["deploys"]);
        var notificationId = (await GetAsync(a.Id)).NotificationId!.Value;

        // Re-dispatching the notification intent (as after a crash before ProcessedAt) publishes nothing new.
        InScope(db => db.ApprovalOutbox.Where(o => o.ApprovalId == a.Id).ExecuteUpdate(s => s.SetProperty(o => o.ProcessedAt, (DateTimeOffset?)null)));
        await InScopeAsync(sp => sp.GetRequiredService<ApprovalService>().DispatchDueAsync(100, CancellationToken.None));
        Assert.Equal(1, InScope(db => db.Notifications.Count(x => x.Id == notificationId)));

        // The same decision submitted twice (double click, retried request) is one decision.
        var browser = await _factory.LoginAsync(_member);
        var first = await DecideAsync(_member, a.Id, ApprovalDecisions.Approve, key: "decision-1", browser: browser);
        var second = await DecideAsync(_member, a.Id, ApprovalDecisions.Approve, key: "decision-1", browser: browser);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await second.ReadJson<ApprovalResource>()).Version);
        Assert.Single(Audit(a.Id), x => x.Action == ApprovalAuditActions.Approved);
        // The same key cannot be replayed into a different decision.
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(_member, a.Id, ApprovalDecisions.Reject, key: "decision-1", browser: browser)).StatusCode);

        // ApprovalResolved delivered repeatedly is consumed once.
        for (var i = 0; i < 3; i++)
        {
            InScope(db => db.ApprovalOutbox.Where(o => o.ApprovalId == a.Id && o.Kind == ApprovalOutboxKinds.Resolved)
                .ExecuteUpdate(s => s.SetProperty(o => o.ProcessedAt, (DateTimeOffset?)null).SetProperty(o => o.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1))));
            await InScopeAsync(sp => sp.GetRequiredService<ApprovalService>().DispatchDueAsync(100, CancellationToken.None));
        }
        Assert.Single(Audit(a.Id), x => x.Action == ApprovalAuditActions.OutcomeAvailable);
        Assert.Equal(ApprovalStatuses.Approved, (await GetAsync(a.Id)).Status);
    }

    [Fact]
    public async Task Notification_is_skipped_when_the_approval_resolved_before_publication()
    {
        _notifier.Fail = true;
        ApprovalResource a;
        try { a = await CreateAsync(tags: ["deploys"]); }
        finally { _notifier.Fail = false; }
        Assert.Equal(HttpStatusCode.OK, (await _factory.ApiClient(_ws.ApiKey).PostAsync($"/api/v1/approvals/{a.Id}/cancel", null)).StatusCode);
        _clock.Offset = TimeSpan.FromMinutes(1);
        try { await InScopeAsync(sp => sp.GetRequiredService<ApprovalService>().DispatchDueAsync(100, CancellationToken.None)); }
        finally { _clock.Offset = TimeSpan.Zero; }
        Assert.Contains(Audit(a.Id), x => x.Action == ApprovalAuditActions.NotificationSkipped);
        Assert.Null((await GetAsync(a.Id)).NotificationId);
    }

    // ------------------------------------------------------------------ 13. existing features alongside

    [Fact]
    public async Task Ordinary_publishing_is_unaffected()
    {
        var res = await _factory.ApiClient(_ws.ApiKey).PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds"], "Build 812 green"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(0, InScope(db => db.Approvals.Count(a => a.Subject == "Build 812 green")));
    }
}
