using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using HashRelay.Client;
using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using HashRelay.Server.Services;
using HashRelay.Server.Services.Approvals;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>Reversible stand-in for DPAPI, so tests can check that nothing is written in plaintext.</summary>
public sealed class XorProtector : IGrantProtector
{
    public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
    public byte[] Unprotect(byte[] sealedBytes) => Protect(sealedBytes);
}

/// <summary>
/// One-click native approvals (ADL requirement DeviceDecisionAuthority): a paired device decides only with a decision
/// grant its own pairer gave after signing in, only from the approval's own notification delivered to it, and only as
/// that person under every check the page applies. Real server, real SQLite, a real paired device receiving over the hub.
/// </summary>
public sealed class OneClickApprovalTests : IAsyncLifetime
{
    private const string Tag = "ops";
    private readonly ShiftableClock _clock = new();
    private readonly RelayFactory _factory;
    private RelayFactory.Workspace _ws = null!;     // owner pairs the PC and is an approver
    private RelayFactory.Workspace _member = null!; // second member of _ws
    private RelayFactory.Workspace _other = null!;  // unrelated workspace
    private readonly List<IAsyncDisposable> _hubs = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hr-oneclick-" + Guid.NewGuid().ToString("N"));

    public OneClickApprovalTests()
    {
        _factory = new RelayFactory
        {
            Settings = { ["HashRelay:Approvals:SweepIntervalSeconds"] = "0" },
            TestServices = s => { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(_clock); },
        };
    }

    public async ValueTask InitializeAsync()
    {
        _ws = await _factory.CreateWorkspaceAsync("One-click");
        _member = await _factory.CreateWorkspaceAsync("Member home");
        _other = await _factory.CreateWorkspaceAsync("Elsewhere");
        await InDbAsync(db => db.Memberships.Add(new Membership { TenantId = _ws.TenantId, AccountId = _member.AccountId, Role = Roles.Member, CreatedAt = DateTimeOffset.UtcNow }));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hubs) await h.DisposeAsync();
        _factory.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ------------------------------------------------------------------ helpers

    private async Task InDbAsync(Action<RelayDbContext> f)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        f(db);
        await db.SaveChangesAsync();
    }

    private T InDb<T>(Func<RelayDbContext, T> f)
    {
        using var scope = _factory.Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<RelayDbContext>());
    }

    private sealed record Device(string Token, string EndpointId, ChannelReader<NotificationMessage> Inbox);

    /// <summary>Pairs a PC through the real device-authorization flow (approved by <paramref name="by"/>) and brings it online subscribed to #ops.</summary>
    private async Task<Device> PairOnlineAsync(RelayFactory.Workspace by, string endpointId = "pc-1")
    {
        var anon = _factory.ApiClient(null);
        var code = await (await anon.PostAsJsonAsync("/api/v1/device/code", new DeviceCodeRequest(endpointId, "Office PC", "windows", "0.1.0", "agent"))).ReadJson<DeviceCodeResponse>();
        using (var scope = _factory.Services.CreateScope())
        {
            var pairing = scope.ServiceProvider.GetRequiredService<DevicePairingService>();
            var p = await pairing.FindPendingAsync(code.UserCode, CancellationToken.None);
            await pairing.ApproveAsync(p!.Id, by.AccountId, by.TenantId, [Scopes.Subscribe], CancellationToken.None);
        }
        var token = (await (await anon.PostAsJsonAsync("/api/v1/device/token", new DeviceTokenRequest(code.DeviceCode))).ReadJson<DeviceTokenResponse>()).Token!;

        var inbox = Channel.CreateUnbounded<NotificationMessage>();
        var hub = _factory.Hub(token);
        hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => inbox.Writer.TryWrite(m));
        await hub.StartAsync();
        _hubs.Add(hub);
        await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest(endpointId, "Office PC", "windows", "test"));
        await hub.InvokeAsync<IReadOnlyList<string>>(HubMethods.Subscribe, new[] { Tag });
        return new Device(token, endpointId, inbox.Reader);
    }

    private static string Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>The device asks; <paramref name="person"/> signs in, ticks the warning and grants; the device collects the secret.</summary>
    private async Task<string> EnrollAsync(Device d, RelayFactory.Workspace person)
    {
        var start = await GrantRequestAsync(d);
        var res = await GrantInBrowserAsync(person, start.UserCode);
        Assert.Contains("One-click approvals on", res);
        var poll = await PollAsync(d, start.PollCode);
        Assert.Equal("granted", poll.Status);
        Assert.StartsWith(DecisionGrants.SecretPrefix, poll.Grant);
        return poll.Grant!;
    }

    private async Task<DecisionGrantStart> GrantRequestAsync(Device d)
    {
        var res = await _factory.ApiClient(d.Token).PostAsync("/api/v1/decision-grants", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var start = await res.ReadJson<DecisionGrantStart>();
        Assert.Contains("/approvals/devices?code=" + start.UserCode, start.VerificationUri);
        return start;
    }

    private async Task<string> GrantInBrowserAsync(RelayFactory.Workspace person, string userCode, bool understood = true)
    {
        var browser = await _factory.LoginAsync(person);
        var html = await browser.GetStringAsync($"/approvals/devices?code={userCode}");
        var grantId = Regex.Match(html, "name=\"grantId\" value=\"([^\"]+)\"").Groups[1].Value;
        if (grantId == "") return html; // no grant form shown (not the pairer, expired)
        Assert.Contains("Anyone who can use this computer while it is unlocked", html);
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(html), ["grantId"] = grantId, ["code"] = userCode };
        if (understood) form["understood"] = "true";
        var post = await browser.PostAsync("/approvals/devices?handler=Grant", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        return await post.Content.ReadAsStringAsync();
    }

    private async Task<DecisionGrantPoll> PollAsync(Device d, string pollCode)
        => await (await _factory.ApiClient(d.Token).PostAsJsonAsync("/api/v1/decision-grants/poll", new DecisionGrantPollRequest(pollCode))).ReadJson<DecisionGrantPoll>();

    private async Task<ApprovalResource> CreateApprovalAsync(Guid[]? approvers = null, DateTimeOffset? expires = null, RelayFactory.Workspace? requester = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/approvals")
        {
            Content = JsonContent.Create(new
            {
                subject = "Deploy version 1.2.3 to production", targetUserIds = approvers ?? [_ws.AccountId], tags = new[] { Tag },
                expiresAt = expires ?? DateTimeOffset.UtcNow.AddMinutes(15),
            }),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var res = await _factory.ApiClient((requester ?? _ws).ApiKey).SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await res.ReadJson<ApprovalResource>();
    }

    /// <summary>The toast the device received for this approval: what the buttons are built from.</summary>
    private static async Task<NotificationMessage> ToastForAsync(Device d, Guid approvalId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var m = await d.Inbox.ReadAsync(cts.Token);
            if (m.Url?.EndsWith($"/approvals/{approvalId}") == true) return m;
        }
    }

    private async Task<HttpResponseMessage> DeviceDecideAsync(Device d, string? secret, Guid approvalId, string decision, Guid deliveryId, string? key = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/approvals/{approvalId}/device-decision")
        {
            Content = JsonContent.Create(new DeviceDecisionRequest(decision, deliveryId)),
        };
        if (secret is not null) req.Headers.Add(DecisionGrants.Header, secret);
        req.Headers.Add("Idempotency-Key", key ?? ApprovalToastArgs.IdempotencyKey(approvalId, decision));
        return await _factory.ApiClient(d.Token).SendAsync(req);
    }

    private async Task<ApprovalResource> GetAsync(Guid id)
        => await (await _factory.ApiClient(_ws.ApiKey).GetAsync($"/api/v1/approvals/{id}")).ReadJson<ApprovalResource>();

    private static async Task<ApiError> ErrorOf(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<ApiError>())!;

    private List<ApprovalAudit> Audit(Guid id) => InDb(db => db.ApprovalAudits.AsNoTracking().Where(a => a.ApprovalId == id).ToList());

    private HashRelayApi ClientApi(string token, HttpMessageHandler? handler = null)
        => new(new Uri("http://localhost/"), token, handler is null ? _factory.CreateClient() : new HttpClient(handler));

    private DecisionGrantStore NewStore() => new(Path.Combine(_dir, "decision-grant.bin"), new XorProtector());

    // ------------------------------------------------------------------ 1 and 2. authorized decisions

    [Fact]
    public async Task Authorized_approve_records_the_grant_holders_decision()
    {
        var pc = await PairOnlineAsync(_ws);
        var secret = await EnrollAsync(pc, _ws);
        var a = await CreateApprovalAsync();
        var toast = await ToastForAsync(pc, a.Id);

        var res = await DeviceDecideAsync(pc, secret, a.Id, ApprovalDecisions.Approve, toast.DeliveryId);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = await res.ReadJson<ApprovalResource>();
        Assert.Equal(ApprovalStatuses.Approved, r.Status);
        Assert.True(r.Proceed);
        Assert.Equal(_ws.AccountId, r.Resolution!.DecidedBy);
        Assert.Equal(ApprovalStatuses.Approved, (await GetAsync(a.Id)).Status);
        Assert.Single(Audit(a.Id), x => x.Action == ApprovalAuditActions.Approved && x.ActorId == _ws.AccountId && x.Detail!.Contains("one-click on Office PC"));
        Assert.NotNull(InDb(db => db.DecisionGrants.AsNoTracking().Single(g => g.SecretHash != null).LastUsedAt));
    }

    [Fact]
    public async Task Authorized_reject_records_the_grant_holders_decision()
    {
        var pc = await PairOnlineAsync(_ws);
        var secret = await EnrollAsync(pc, _ws);
        var a = await CreateApprovalAsync();
        var toast = await ToastForAsync(pc, a.Id);

        var res = await DeviceDecideAsync(pc, secret, a.Id, ApprovalDecisions.Reject, toast.DeliveryId);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = await res.ReadJson<ApprovalResource>();
        Assert.Equal(ApprovalStatuses.Rejected, r.Status);
        Assert.False(r.Proceed);
        Assert.Single(Audit(a.Id), x => x.Action == ApprovalAuditActions.Rejected && x.ActorId == _ws.AccountId);
    }

    // ------------------------------------------------------------------ 3. the pairing token alone never decides

    [Fact]
    public async Task A_device_token_alone_cannot_decide()
    {
        var pc = await PairOnlineAsync(_ws);
        var a = await CreateApprovalAsync();
        var toast = await ToastForAsync(pc, a.Id);

        // No grant header.
        var none = await DeviceDecideAsync(pc, null, a.Id, ApprovalDecisions.Approve, toast.DeliveryId);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal([DecisionGrants.Invalid], (await ErrorOf(none)).Details!["grant"]);

        // A made-up secret.
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeviceDecideAsync(pc, DecisionGrants.SecretPrefix + "guess", a.Id, ApprovalDecisions.Approve, toast.DeliveryId)).StatusCode);

        // A request the person has not granted yet yields nothing to decide with.
        var start = await GrantRequestAsync(pc);
        Assert.Equal("pending", (await PollAsync(pc, start.PollCode)).Status);

        // The page's cookie route ignores a bearer device token.
        using var cookieRoute = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/approvals/{a.Id}/decision") { Content = JsonContent.Create(new { decision = "approve" }) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ApiClient(pc.Token).SendAsync(cookieRoute)).StatusCode);

        // Application keys are not devices.
        using var apiKey = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/approvals/{a.Id}/device-decision") { Content = JsonContent.Create(new DeviceDecisionRequest("approve", toast.DeliveryId)) };
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ApiClient(_ws.ApiKey).SendAsync(apiKey)).StatusCode);

        // A grant secret is bound to its own device credential: useless with another device's token.
        var secret = await EnrollAsync(pc, _ws);
        var laptop = await PairOnlineAsync(_ws, "laptop-2");
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeviceDecideAsync(laptop, secret, a.Id, ApprovalDecisions.Approve, toast.DeliveryId)).StatusCode);

        Assert.Equal(ApprovalStatuses.Pending, (await GetAsync(a.Id)).Status);
    }

    [Fact]
    public async Task Enrolment_needs_the_pairer_signed_in_and_the_risk_acknowledged()
    {
        var pc = await PairOnlineAsync(_ws);

        // Another member of the workspace cannot grant the owner's PC, nor deny it.
        var start = await GrantRequestAsync(pc);
        var html = await GrantInBrowserAsync(_member, start.UserCode);
        Assert.Contains("Only the person who paired", html);
        Assert.Equal("pending", (await PollAsync(pc, start.PollCode)).Status);

        // The service enforces the same rule without the page.
        var grantId = InDb(db => db.DecisionGrants.AsNoTracking().Single(g => g.UserCode == start.UserCode).Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ApprovalService>();
            var ex = await Assert.ThrowsAsync<ApiException>(() => svc.GrantDeviceDecisionsAsync(_member.AccountId, grantId, CancellationToken.None));
            Assert.Equal(403, ex.Status);
            await svc.DenyDecisionGrantAsync(_member.AccountId, grantId, CancellationToken.None);
        }
        Assert.Equal("pending", (await PollAsync(pc, start.PollCode)).Status);

        // Not ticking the warning grants nothing.
        var unticked = await GrantInBrowserAsync(_ws, start.UserCode, understood: false);
        Assert.Contains("Tick the box", unticked);
        Assert.Equal("pending", (await PollAsync(pc, start.PollCode)).Status);

        // Signed out, the page sends you to sign in.
        var anon = _factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync($"/approvals/devices?code={start.UserCode}")).StatusCode);

        // The pairer grants; the secret is handed over exactly once.
        await GrantInBrowserAsync(_ws, start.UserCode);
        Assert.Equal("granted", (await PollAsync(pc, start.PollCode)).Status);
        Assert.Equal("expired", (await PollAsync(pc, start.PollCode)).Status);
        var row = InDb(db => db.DecisionGrants.AsNoTracking().Single(g => g.Id == grantId));
        Assert.Null(row.IssuedSecret);
        Assert.Equal(_ws.AccountId, row.AccountId);
        Assert.Contains(InDb(db => db.AuditEvents.AsNoTracking().Where(e => e.TenantId == _ws.TenantId).Select(e => e.Action).ToList()), x => x == AuditActions.DecisionGrantCreated);
    }

    // ------------------------------------------------------------------ 4. not a designated approver; look-alike notifications

    [Fact]
    public async Task A_grant_holder_who_is_not_a_designated_approver_is_refused()
    {
        var pc = await PairOnlineAsync(_ws);
        var secret = await EnrollAsync(pc, _ws);
        var a = await CreateApprovalAsync(approvers: [_member.AccountId]);
        var toast = await ToastForAsync(pc, a.Id);

        var res = await DeviceDecideAsync(pc, secret, a.Id, ApprovalDecisions.Approve, toast.DeliveryId);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(ApprovalStatuses.Pending, (await GetAsync(a.Id)).Status);
    }

    [Fact]
    public async Task A_look_alike_notification_linking_a_real_approval_cannot_decide_it()
    {
        var pc = await PairOnlineAsync(_ws);
        var secret = await EnrollAsync(pc, _ws);
        var a = await CreateApprovalAsync();
        _ = await ToastForAsync(pc, a.Id);

        // Anyone who may publish to #ops sends a toast with the real approval's link and misleading text.
        var pub = await _factory.ApiClient(_ws.ApiKey).PostAsJsonAsync("/api/v1/notifications",
            new PublishRequest([Tag], "Tap Approve to dismiss this reminder", null, a.DetailUrl, Priorities.High));
        Assert.True(pub.IsSuccessStatusCode);
        var fake = await ToastForAsync(pc, a.Id);
        Assert.NotEqual(a.Id, fake.NotificationId);

        var res = await DeviceDecideAsync(pc, secret, a.Id, ApprovalDecisions.Approve, fake.DeliveryId);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("own notification", (await ErrorOf(res)).Message);
        Assert.Equal(ApprovalStatuses.Pending, (await GetAsync(a.Id)).Status);
    }

    // ------------------------------------------------------------------ 5. revocation and grant expiry

    [Fact]
    public async Task A_revoked_grant_cannot_decide()
    {
        var pc = await PairOnlineAsync(_ws);
        var secret = await EnrollAsync(pc, _ws);
        var a = await CreateApprovalAsync();
        var toast = await ToastForAsync(pc, a.Id);

        // Revoked by the person on the website.
        var browser = await _factory.LoginAsync(_ws);
        var html = await browser.GetStringAsync("/approvals/devices");
        Assert.Contains("data-grant-list", html);
        var grantId = Regex.Match(html, "name=\"grantId\" value=\"([^\"]+)\"").Groups[1].Value;
        var revoke = await browser.PostAsync("/approvals/devices?handler=Revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html), ["grantId"] = grantId,
        }));
        Assert.Contains("One-click approvals turned off", await revoke.Content.ReadAsStringAsync());

        var res = await DeviceDecideAsync(pc, secret, a.Id, ApprovalDecisions.Approve, toast.DeliveryId);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal([DecisionGrants.Invalid], (await ErrorOf(res)).Details!["grant"]);

        // Revoked from the device itself (tray: Disable one-click approvals).
        var again = await EnrollAsync(pc, _ws);
        using (var api = ClientApi(pc.Token)) await api.RevokeDecisionGrantAsync(again);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeviceDecideAsync(pc, again, a.Id, ApprovalDecisions.Approve, toast.DeliveryId)).StatusCode);
        Assert.Contains(InDb(db => db.AuditEvents.AsNoTracking().Where(e => e.TenantId == _ws.TenantId).Select(e => e.Action).ToList()), x => x == AuditActions.DecisionGrantRevoked);

        // A new grant replaces an earlier one for the same device.
        var first = await EnrollAsync(pc, _ws);
        var second = await EnrollAsync(pc, _ws);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeviceDecideAsync(pc, first, a.Id, ApprovalDecisions.Approve, toast.DeliveryId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DeviceDecideAsync(pc, second, a.Id, ApprovalDecisions.Approve, toast.DeliveryId)).StatusCode);
    }

    [Fact]
    public async Task An_expired_grant_cannot_decide()
    {
        var pc = await PairOnlineAsync(_ws);
        var secret = await EnrollAsync(pc, _ws);
        var a = await CreateApprovalAsync(expires: DateTimeOffset.UtcNow.AddDays(29));
        var toast = await ToastForAsync(pc, a.Id);
        _clock.Offset = DecisionGrants.GrantLifetime + TimeSpan.FromDays(1);
        try
        {
            var res = await DeviceDecideAsync(pc, secret, a.Id, ApprovalDecisions.Approve, toast.DeliveryId);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        finally { _clock.Offset = TimeSpan.Zero; }
        Assert.DoesNotContain(Audit(a.Id), x => x.Action is ApprovalAuditActions.Approved or ApprovalAuditActions.Rejected);
    }

    // ------------------------------------------------------------------ 6. expired approval

    [Fact]
    public async Task An_expired_approval_cannot_be_decided_by_one_click()
    {
        var pc = await PairOnlineAsync(_ws);
        var secret = await EnrollAsync(pc, _ws);
        var a = await CreateApprovalAsync(expires: DateTimeOffset.UtcNow.AddMinutes(2));
        var toast = await ToastForAsync(pc, a.Id);
        _clock.Offset = TimeSpan.FromMinutes(3);
        try
        {
            var res = await DeviceDecideAsync(pc, secret, a.Id, ApprovalDecisions.Approve, toast.DeliveryId);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal([ApprovalStatuses.Expired], (await ErrorOf(res)).Details!["status"]);
            Assert.Equal(ApprovalStatuses.Expired, (await GetAsync(a.Id)).Status);
        }
        finally { _clock.Offset = TimeSpan.Zero; }
    }

    // ------------------------------------------------------------------ 7 and 8. concurrency and repeated clicks

    [Fact]
    public async Task Concurrent_one_click_and_page_decisions_produce_one_terminal_state()
    {
        var pc = await PairOnlineAsync(_ws);
        var secret = await EnrollAsync(pc, _ws);
        for (var round = 0; round < 3; round++)
        {
            var a = await CreateApprovalAsync(approvers: [_ws.AccountId, _member.AccountId]);
            var toast = await ToastForAsync(pc, a.Id);
            async Task<HttpStatusCode> Page(Guid account, string decision)
            {
                using var scope = _factory.Services.CreateScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<ApprovalService>().DecideAsync(account, a.Id, new ApprovalDecisionRequest(decision), null, null, CancellationToken.None);
                    return HttpStatusCode.OK;
                }
                catch (ApiException ex) { return (HttpStatusCode)ex.Status; }
            }
            async Task<HttpStatusCode> Click(string decision) => (await DeviceDecideAsync(pc, secret, a.Id, decision, toast.DeliveryId)).StatusCode;

            var results = await Task.WhenAll(Click(ApprovalDecisions.Approve), Page(_member.AccountId, ApprovalDecisions.Reject),
                Click(ApprovalDecisions.Reject), Page(_ws.AccountId, ApprovalDecisions.Approve));
            Assert.Single(results, r => r == HttpStatusCode.OK);
            Assert.All(results.Where(r => r != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r));
            var final = await GetAsync(a.Id);
            Assert.Equal(2, final.Version);
            Assert.Single(Audit(a.Id), x => x.Action is ApprovalAuditActions.Approved or ApprovalAuditActions.Rejected);
        }
    }

    [Fact]
    public async Task Repeated_clicks_record_one_decision()
    {
        var pc = await PairOnlineAsync(_ws);
        var secret = await EnrollAsync(pc, _ws);
        var a = await CreateApprovalAsync();
        var toast = await ToastForAsync(pc, a.Id);

        // The same button pressed twice, or retried by a second agent process: same Idempotency-Key, same outcome.
        var first = await (await DeviceDecideAsync(pc, secret, a.Id, ApprovalDecisions.Approve, toast.DeliveryId)).ReadJson<ApprovalResource>();
        var second = await DeviceDecideAsync(pc, secret, a.Id, ApprovalDecisions.Approve, toast.DeliveryId);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(first.Version, (await second.ReadJson<ApprovalResource>()).Version);

        // The other button afterwards: already resolved, nothing new.
        var other = await DeviceDecideAsync(pc, secret, a.Id, ApprovalDecisions.Reject, toast.DeliveryId);
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal([ApprovalStatuses.Approved], (await ErrorOf(other)).Details!["status"]);

        Assert.Equal(2, (await GetAsync(a.Id)).Version);
        Assert.Single(Audit(a.Id), x => x.Action is ApprovalAuditActions.Approved or ApprovalAuditActions.Rejected);
    }

    // ------------------------------------------------------------------ 9. offline never reports success (client)

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("No such host is known.");
    }

    private sealed class ServerError : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") });
    }

    [Fact]
    public async Task Offline_or_failed_submission_never_reports_success()
    {
        var store = NewStore();
        store.Save(new StoredDecisionGrant("http://localhost/", DecisionGrants.SecretPrefix + "x", "Tester", DateTimeOffset.UtcNow.AddDays(1)));
        var server = new Uri("http://localhost/");
        foreach (HttpMessageHandler h in new HttpMessageHandler[] { new Offline(), new ServerError() })
        {
            var decider = new ApprovalDecider(() => ClientApi("hr_dev_x", h), server, store);
            var r = await decider.DecideAsync(Guid.NewGuid(), ApprovalDecisions.Approve, Guid.NewGuid());
            Assert.Equal(DecisionResultKind.Failed, r.Kind);
            Assert.False(r.Decided);
            Assert.True(r.Retryable);
            Assert.Contains("Nothing was recorded", r.Message);
        }
        Assert.NotNull(store.Load()); // a network failure does not forget the grant

        // Not enrolled: nothing is sent, and the toast says to open HashRelay.
        var empty = new DecisionGrantStore(Path.Combine(_dir, "none.bin"), new XorProtector());
        var ne = await new ApprovalDecider(() => throw new InvalidOperationException("must not call"), server, empty).DecideAsync(Guid.NewGuid(), "approve", Guid.NewGuid());
        Assert.Equal(DecisionResultKind.NotEnrolled, ne.Kind);
    }

    // ------------------------------------------------------------------ 10. the page flow is unchanged

    [Fact]
    public async Task The_browser_flow_still_works_with_one_click_enabled()
    {
        var pc = await PairOnlineAsync(_ws);
        _ = await EnrollAsync(pc, _ws);
        var a = await CreateApprovalAsync(approvers: [_ws.AccountId, _member.AccountId]);

        var browser = await _factory.LoginAsync(_member);
        var html = await browser.GetStringAsync($"/approvals/{a.Id}?decision=approve");
        var key = Regex.Match(html, "name=\"Key\" value=\"([^\"]+)\"").Groups[1].Value;
        var post = await browser.PostAsync($"/approvals/{a.Id}?handler=Approve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html), ["Key"] = key,
        }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Contains("You approved this request.", await post.Content.ReadAsStringAsync());
        var r = await GetAsync(a.Id);
        Assert.Equal((ApprovalStatuses.Approved, (Guid?)_member.AccountId), (r.Status, r.Resolution!.DecidedBy));

        // The page route still refuses a session without its antiforgery token.
        using var bare = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/approvals/{a.Id}/decision") { Content = JsonContent.Create(new { decision = "reject" }) };
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(bare)).StatusCode);
    }

    // ------------------------------------------------------------------ 11. a fresh process handles the click (client)

    [Fact]
    public async Task A_fresh_process_handles_the_click_from_the_sealed_grant_and_the_toast_arguments_alone()
    {
        var pc = await PairOnlineAsync(_ws);
        var server = new Uri("http://localhost/");

        // Enrolment through the client, as the tray menu runs it; the browser step is the signed-in person.
        var store = NewStore();
        Task? browser = null;
        StoredDecisionGrant? enrolled;
        using (var api = ClientApi(pc.Token))
            enrolled = await DecisionGrantEnrollment.RunAsync(api, server, store, (uri, code) => browser = GrantInBrowserAsync(_ws, code),
                CancellationToken.None, interval: TimeSpan.FromMilliseconds(50));
        await browser!;
        Assert.NotNull(enrolled);
        Assert.Equal("Tester", enrolled!.AccountName);
        var onDisk = await File.ReadAllBytesAsync(store.FilePath);
        Assert.DoesNotContain(enrolled.Secret, Encoding.UTF8.GetString(onDisk)); // sealed, not plaintext
        Assert.DoesNotContain(enrolled.Secret, await File.ReadAllTextAsync(Path.Combine(_dir, "decision-grant.bin")));

        var a = await CreateApprovalAsync();
        var toast = await ToastForAsync(pc, a.Id);
        // The toast carries ids only.
        var args = ApprovalToastArgs.For(a.Id, ApprovalDecisions.Approve, toast.DeliveryId);
        Assert.DoesNotContain(args.Values, v => v.Contains(DecisionGrants.SecretPrefix) || v.Contains("hr_dev_"));

        // "Restart": nothing in memory survives; a new store over the same file and the arguments Windows passes back.
        var restarted = new ApprovalDecider(() => ClientApi(pc.Token), server, new DecisionGrantStore(store.FilePath, new XorProtector()));
        Assert.True(restarted.IsEnrolled);
        var passedBack = args.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.True(ApprovalToastArgs.TryParse(passedBack, out var id, out var decision, out var delivery));
        var r = await restarted.DecideAsync(id, decision, delivery);
        Assert.Equal(DecisionResultKind.Approved, r.Kind);
        Assert.Equal(ApprovalStatuses.Approved, (await GetAsync(a.Id)).Status);

        // The same click delivered again (e.g. both the running agent and a relaunched one) is not a second decision.
        var again = await restarted.DecideAsync(id, decision, delivery);
        Assert.Equal(DecisionResultKind.Approved, again.Kind);
        Assert.Equal(2, (await GetAsync(a.Id)).Version);

        // The other button: reported as already resolved, not as success.
        var reject = await restarted.DecideAsync(id, ApprovalDecisions.Reject, delivery);
        Assert.Equal(DecisionResultKind.AlreadyResolved, reject.Kind);
        Assert.False(reject.Decided);

        // Revoked on the server: the next click says so and the device stops offering one-click.
        using (var api = ClientApi(pc.Token)) await DecisionGrantEnrollment.DisableAsync(api, store, CancellationToken.None);
        Assert.False(File.Exists(store.FilePath));
        var b = await CreateApprovalAsync();
        var t2 = await ToastForAsync(pc, b.Id);
        store.Save(enrolled); // a stale copy of the revoked grant
        var stale = await new ApprovalDecider(() => ClientApi(pc.Token), server, store).DecideAsync(b.Id, ApprovalDecisions.Approve, t2.DeliveryId);
        Assert.Equal(DecisionResultKind.NotAuthorized, stale.Kind);
        Assert.False(File.Exists(store.FilePath));
        Assert.Equal(ApprovalStatuses.Pending, (await GetAsync(b.Id)).Status);
    }

    [Fact]
    public void Toast_arguments_must_name_an_approval_a_decision_and_a_delivery()
    {
        var id = Guid.NewGuid();
        var del = Guid.NewGuid();
        var ok = ApprovalToastArgs.For(id, ApprovalDecisions.Reject, del).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.True(ApprovalToastArgs.TryParse(ok, out var pid, out var pd, out var pdel));
        Assert.Equal((id, "reject", del), (pid, pd, pdel));
        Assert.Equal($"toast-{id:N}-reject", ApprovalToastArgs.IdempotencyKey(id, "reject"));

        Assert.False(ApprovalToastArgs.TryParse(new Dictionary<string, string>(ok) { ["decision"] = "delete" }, out _, out _, out _));
        Assert.False(ApprovalToastArgs.TryParse(new Dictionary<string, string>(ok) { ["action"] = "url" }, out _, out _, out _));
        Assert.False(ApprovalToastArgs.TryParse(new Dictionary<string, string>(ok) { ["deliveryId"] = "x" }, out _, out _, out _));
        Assert.False(ApprovalToastArgs.TryParse(new Dictionary<string, string>(ok) { ["approvalId"] = "x" }, out _, out _, out _));
    }

    // ------------------------------------------------------------------ 12. cross-workspace

    [Fact]
    public async Task A_grant_cannot_decide_an_approval_in_another_workspace()
    {
        // The owner of _ws is also a designated approver in _other, and a member there.
        await InDbAsync(db => db.Memberships.Add(new Membership { TenantId = _other.TenantId, AccountId = _ws.AccountId, Role = Roles.Member, CreatedAt = DateTimeOffset.UtcNow }));
        var pc = await PairOnlineAsync(_ws);
        var secret = await EnrollAsync(pc, _ws);
        var mine = await CreateApprovalAsync();
        var toast = await ToastForAsync(pc, mine.Id);
        var theirs = await CreateApprovalAsync(approvers: [_ws.AccountId], requester: _other);

        var res = await DeviceDecideAsync(pc, secret, theirs.Id, ApprovalDecisions.Approve, toast.DeliveryId);
        Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, res.StatusCode.ToString());
        var read = await (await _factory.ApiClient(_other.ApiKey).GetAsync($"/api/v1/approvals/{theirs.Id}")).ReadJson<ApprovalResource>();
        Assert.Equal(ApprovalStatuses.Pending, read.Status);

        // And the service refuses it even with a genuine delivery check bypassed: the grant's workspace scopes DecideAsync.
        using var scope = _factory.Services.CreateScope();
        var ex = await Assert.ThrowsAsync<ApiException>(() => scope.ServiceProvider.GetRequiredService<ApprovalService>()
            .DecideAsync(_ws.AccountId, theirs.Id, new ApprovalDecisionRequest("approve"), null, null, CancellationToken.None, tenantScope: _ws.TenantId));
        Assert.Equal(404, ex.Status);
    }
}
