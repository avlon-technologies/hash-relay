using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HashRelay.Contracts;
using HashRelay.Server.Data;
using HashRelay.Server.Services;
using HashRelay.Server.Services.Approvals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>
/// MCP request_approval / get_approval / list_approvers, driven with the official MCP client against the real server.
/// Human decisions go through the real sign-in, decision page and antiforgery token, never through MCP.
/// </summary>
public sealed partial class McpApprovalTests : IAsyncLifetime
{
    private readonly ShiftableClock _clock = new();
    private readonly RelayFactory _factory;
    private RelayFactory.Workspace _ws = null!;      // the agent's workspace; its owner created the service account
    private RelayFactory.Workspace _member = null!;  // a second member of _ws
    private RelayFactory.Workspace _other = null!;   // an unrelated workspace

    public McpApprovalTests()
    {
        _factory = new RelayFactory
        {
            Settings = { ["HashRelay:Approvals:SweepIntervalSeconds"] = "0" },
            TestServices = s => { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(_clock); },
        };
    }

    public async ValueTask InitializeAsync()
    {
        _ws = await _factory.CreateWorkspaceAsync("Agent Co");
        _member = await _factory.CreateWorkspaceAsync("Member home");
        _other = await _factory.CreateWorkspaceAsync("Elsewhere");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        db.Memberships.Add(new Membership { TenantId = _ws.TenantId, AccountId = _member.AccountId, Role = Roles.Member, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    // ------------------------------------------------------------------ helpers

    private async Task<string> ServiceKeyAsync(RelayFactory.Workspace ws, string name, string allowedTags, params string[] scopes)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ServiceAccountService>();
        var account = await svc.CreateAsync(ws.TenantId, ws.AccountId, ServiceAccountService.Validate(name, null, scopes, allowedTags, null, 60), CancellationToken.None);
        return (await svc.IssueKeyAsync(ws.TenantId, account.Id, ws.AccountId, null, null, CancellationToken.None)).Secret;
    }

    private Task<string> AgentKeyAsync(string name = "claude · owner") => ServiceKeyAsync(_ws, name, "test,agent-*", ServiceScopes.ApprovalsCreate, ServiceScopes.Publish);

    private async Task<McpClient> ConnectAsync(string key)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_factory.Server.BaseAddress, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
        }, _factory.CreateClient());
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static async Task<JsonElement> OkAsync(Task<CallToolResult> call)
    {
        var r = await call;
        Assert.True(r.IsError != true, Text(r));
        return JsonDocument.Parse(Text(r)).RootElement.Clone();
    }

    private static async Task<string> FailAsync(Task<CallToolResult> call)
    {
        var r = await call;
        Assert.True(r.IsError == true, "expected a tool error, got: " + Text(r));
        return Text(r);
    }

    private static Dictionary<string, object?> RequestArgs(IEnumerable<Guid> approvers, string key, DateTimeOffset? expires = null, string[]? tags = null,
        string subject = "Rotate the production database password")
        => new()
        {
            ["subject"] = subject,
            ["description"] = "Rotates the primary credential; clients reconnect within 30 s.",
            ["targetUserIds"] = approvers.Select(a => a.ToString()).ToArray(),
            ["tags"] = tags,
            ["expiresAt"] = (expires ?? DateTimeOffset.UtcNow.AddMinutes(15)).ToString("o"),
            ["requesterCorrelationId"] = "chg-42",
            ["idempotencyKey"] = key,
        };

    private static Task<CallToolResult> Request(McpClient c, IEnumerable<Guid> approvers, string? key = null, DateTimeOffset? expires = null, string[]? tags = null)
        => c.CallToolAsync("request_approval", RequestArgs(approvers, key ?? Guid.NewGuid().ToString("N"), expires, tags)).AsTask();

    private static Task<CallToolResult> Get(McpClient c, Guid id)
        => c.CallToolAsync("get_approval", new Dictionary<string, object?> { ["approvalId"] = id.ToString() }).AsTask();

    private static Guid IdOf(JsonElement requested) => requested.GetProperty("approvalId").GetGuid();
    private static JsonElement ApprovalOf(JsonElement state) => state.GetProperty("approval");

    private static string Token(string html)
        => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>A person signs in, opens the approval page and posts the decision with its antiforgery token.</summary>
    private async Task<HttpResponseMessage> HumanDecidesAsync(RelayFactory.Workspace who, Guid id, string decision)
    {
        var browser = await _factory.LoginAsync(who);
        var page = await browser.GetAsync($"/approvals/{id}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/approvals/{id}/decision") { Content = JsonContent.Create(new { decision, note = "checked the runbook" }) };
        var token = Token(await page.Content.ReadAsStringAsync());
        if (token == "") token = Token(await browser.GetStringAsync("/dashboard")); // a resolved approval's page has no form
        req.Headers.Add("RequestVerificationToken", token);
        return await browser.SendAsync(req);
    }

    private Approval Row(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<RelayDbContext>().Approvals.AsNoTracking().First(a => a.Id == id);
    }

    // ------------------------------------------------------------------ discovery and the authorization boundary

    [Fact]
    public async Task Approval_tools_are_listed_beside_the_existing_tools_and_none_can_decide()
    {
        await using var client = await ConnectAsync(await AgentKeyAsync());
        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "publish_notification", "list_tags", "list_notifications", "get_notification", "list_endpoints", "set_subscriptions",
            "request_approval", "get_approval", "list_approvers" }, names);
        Assert.Equal(["get_approval", "list_approvers", "request_approval"], names.Where(n => n.Contains("approv")).Order());
        Assert.DoesNotContain(names, n => n.Contains("reject") || n.Contains("decide") || n.Contains("decision") || n.StartsWith("approve"));

        var request = tools.Single(t => t.Name == "request_approval");
        Assert.Contains("does NOT wait", request.Description);
        Assert.Contains("cannot approve your own request", request.Description);
        Assert.True(tools.Single(t => t.Name == "get_approval").ProtocolTool.Annotations?.ReadOnlyHint);

        // A made-up decision tool does not exist on the server.
        var a = await OkAsync(Request(client, [_ws.AccountId]));
        var fake = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("approve_approval", new Dictionary<string, object?> { ["approvalId"] = IdOf(a).ToString() }).AsTask());
        Assert.Contains("Unknown tool", fake.Message);
        Assert.Equal(ApprovalStatuses.Pending, Row(IdOf(a)).Status);
    }

    [Fact]
    public async Task A_service_account_cannot_decide_its_own_request_by_any_route()
    {
        var key = await AgentKeyAsync();
        await using var client = await ConnectAsync(key);
        var id = IdOf(await OkAsync(Request(client, [_ws.AccountId])));

        // The decision route accepts only a signed-in person's session: the agent's bearer key is unauthenticated there.
        var api = _factory.ApiClient(key);
        foreach (var decision in new[] { "approve", "reject" })
        {
            var res = await api.PostAsJsonAsync($"/api/v1/approvals/{id}/decision", new { decision });
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        // The decision page is a signed-in person's page: a bearer key is not a session (redirect to login, no form).
        var page = await _factory.CreateClient(new() { AllowAutoRedirect = false }).SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/approvals/{id}")
        {
            Headers = { { "Authorization", "Bearer " + key } },
        });
        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);

        var state = ApprovalOf(await OkAsync(Get(client, id)));
        Assert.Equal(ApprovalStatuses.Pending, state.GetProperty("status").GetString());
        Assert.False(state.GetProperty("proceed").GetBoolean());
        Assert.Equal(1, state.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task List_approvers_returns_eligible_members_and_marks_who_connected_the_app()
    {
        await using var client = await ConnectAsync(await AgentKeyAsync());
        var list = (await OkAsync(client.CallToolAsync("list_approvers", new Dictionary<string, object?>()).AsTask())).GetProperty("approvers").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        var first = list[0];
        Assert.Equal(_ws.AccountId, first.GetProperty("accountId").GetGuid());
        Assert.True(first.GetProperty("authorizedThisCredential").GetBoolean());
        Assert.Contains(list, m => m.GetProperty("accountId").GetGuid() == _member.AccountId && !m.GetProperty("authorizedThisCredential").GetBoolean());
        Assert.DoesNotContain(list, m => m.GetProperty("accountId").GetGuid() == _other.AccountId);
        Assert.DoesNotContain("@", list.ToString()); // no email addresses
    }

    // ------------------------------------------------------------------ create, notify, idempotency

    [Fact]
    public async Task Request_approval_persists_pending_and_notifies_devices_subscribed_to_test()
    {
        var api = _factory.ApiClient(_ws.ApiKey);
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsJsonAsync("/api/v1/endpoints", new RegisterEndpointRequest("win-1", "windows", "Desk"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.PutAsync("/api/v1/endpoints/win-1/subscriptions/test", null)).StatusCode);

        await using var client = await ConnectAsync(await AgentKeyAsync());
        var r = await OkAsync(Request(client, [_ws.AccountId], tags: ["#test"]));
        var id = IdOf(r);
        Assert.Equal(ApprovalStatuses.Pending, r.GetProperty("status").GetString());
        Assert.False(r.GetProperty("proceed").GetBoolean());
        Assert.True(r.GetProperty("created").GetBoolean());
        Assert.Equal("chg-42", r.GetProperty("requesterCorrelationId").GetString());
        Assert.Equal($"http://localhost/approvals/{id}", r.GetProperty("approvalUrl").GetString());
        Assert.Contains("do not proceed", r.GetProperty("next").GetString());

        // The one approval store holds it; MCP added no state of its own.
        var row = Row(id);
        Assert.Equal(ApprovalStatuses.Pending, row.Status);
        Assert.Equal(_ws.TenantId, row.TenantId);
        Assert.Equal("claude · owner", row.RequesterName);

        // The linked notification went through ordinary routing to the #test subscriber.
        var notificationId = r.GetProperty("notificationId").GetGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var n = await db.Notifications.AsNoTracking().Include(x => x.Deliveries).FirstAsync(x => x.Id == notificationId);
        Assert.Contains("test", n.Tags);
        Assert.Equal(row.DetailUrl, n.Url);
        Assert.Contains("Rotate the production database password", n.Title);
        Assert.DoesNotContain("reconnect within", n.Body ?? ""); // the description stays behind sign-in
        Assert.Single(n.Deliveries);
        Assert.Equal(1, n.MatchedEndpoints);
    }

    [Fact]
    public async Task Request_approval_is_idempotent_per_key()
    {
        await using var client = await ConnectAsync(await AgentKeyAsync());
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);
        var first = await OkAsync(Request(client, [_ws.AccountId], "retry-1", expires));
        var again = await OkAsync(Request(client, [_ws.AccountId], "retry-1", expires));
        Assert.Equal(IdOf(first), IdOf(again));
        Assert.False(again.GetProperty("created").GetBoolean());

        var different = await FailAsync(Request(client, [_member.AccountId], "retry-1", expires));
        Assert.Contains("different request", different);

        using var scope = _factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<RelayDbContext>().Approvals.CountAsync(a => a.IdempotencyKey == "retry-1"));
    }

    // ------------------------------------------------------------------ authorization and isolation

    [Fact]
    public async Task Permissions_and_workspace_boundaries_are_enforced()
    {
        // No approvals:create.
        await using (var publishOnly = await ConnectAsync(await ServiceKeyAsync(_ws, "publisher", "test", ServiceScopes.Publish)))
            Assert.Contains(ServiceScopes.ApprovalsCreate, await FailAsync(Request(publishOnly, [_ws.AccountId])));

        // A subscribe-only credential is a receiver, not a requester.
        await using (var receiver = await ConnectAsync(_ws.SubscribeOnlyKey))
            Assert.Contains("scope", await FailAsync(Request(receiver, [_ws.AccountId])));

        await using var client = await ConnectAsync(await AgentKeyAsync());
        // Outside the hashtag allowlist.
        Assert.Contains("#finance", await FailAsync(Request(client, [_ws.AccountId], tags: ["finance"])));
        // An approver from another workspace, or a malformed id.
        Assert.Contains("members of this workspace", await FailAsync(Request(client, [_other.AccountId])));
        var malformed = RequestArgs([], "bad");
        malformed["targetUserIds"] = new[] { "the owner" };
        Assert.Contains("UUID", await FailAsync(client.CallToolAsync("request_approval", malformed).AsTask()));
        // Expiry out of range.
        Assert.Contains("expiresAt", await FailAsync(Request(client, [_ws.AccountId], expires: DateTimeOffset.UtcNow.AddSeconds(5))));

        var id = IdOf(await OkAsync(Request(client, [_ws.AccountId])));
        // Another service account in the same workspace, and one in another workspace, see nothing.
        await using (var sibling = await ConnectAsync(await ServiceKeyAsync(_ws, "sibling", "test", ServiceScopes.ApprovalsCreate)))
            Assert.Contains("No such approval", await FailAsync(Get(sibling, id)));
        await using (var foreign = await ConnectAsync(await ServiceKeyAsync(_other, "foreign", "test", ServiceScopes.ApprovalsCreate)))
            Assert.Contains("No such approval", await FailAsync(Get(foreign, id)));
        Assert.Contains("UUID", await FailAsync(client.CallToolAsync("get_approval", new Dictionary<string, object?> { ["approvalId"] = "nope" }).AsTask()));
    }

    // ------------------------------------------------------------------ every state, read through MCP

    [Fact]
    public async Task Get_approval_reports_pending_then_the_human_approval()
    {
        await using var client = await ConnectAsync(await AgentKeyAsync());
        var id = IdOf(await OkAsync(Request(client, [_ws.AccountId, _member.AccountId])));
        Assert.Contains("do not proceed", (await OkAsync(Get(client, id))).GetProperty("guidance").GetString());

        Assert.Equal(HttpStatusCode.OK, (await HumanDecidesAsync(_member, id, ApprovalDecisions.Approve)).StatusCode);

        for (var read = 0; read < 3; read++)
        {
            var state = await OkAsync(Get(client, id));
            var a = ApprovalOf(state);
            Assert.Equal(ApprovalStatuses.Approved, a.GetProperty("status").GetString());
            Assert.True(a.GetProperty("proceed").GetBoolean());
            Assert.Equal(2, a.GetProperty("version").GetInt32());
            var resolution = a.GetProperty("resolution");
            Assert.Equal(ApprovalCauses.Decision, resolution.GetProperty("cause").GetString());
            Assert.Equal(_member.AccountId, resolution.GetProperty("decidedBy").GetGuid());
            Assert.Equal("checked the runbook", resolution.GetProperty("note").GetString());
            Assert.Contains("You may now perform", state.GetProperty("guidance").GetString());
        }

        // A second decision cannot change the terminal outcome.
        Assert.Equal(HttpStatusCode.Conflict, (await HumanDecidesAsync(_ws, id, ApprovalDecisions.Reject)).StatusCode);
        Assert.Equal(ApprovalStatuses.Approved, ApprovalOf(await OkAsync(Get(client, id))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Get_approval_reports_rejected_expired_and_cancelled()
    {
        var key = await AgentKeyAsync();
        await using var client = await ConnectAsync(key);

        var rejected = IdOf(await OkAsync(Request(client, [_ws.AccountId])));
        Assert.Equal(HttpStatusCode.OK, (await HumanDecidesAsync(_ws, rejected, ApprovalDecisions.Reject)).StatusCode);
        var r = ApprovalOf(await OkAsync(Get(client, rejected)));
        Assert.Equal(ApprovalStatuses.Rejected, r.GetProperty("status").GetString());
        Assert.False(r.GetProperty("proceed").GetBoolean());

        var expiring = IdOf(await OkAsync(Request(client, [_ws.AccountId], expires: DateTimeOffset.UtcNow.AddMinutes(2))));
        _clock.Offset = TimeSpan.FromMinutes(3);
        try
        {
            var e = ApprovalOf(await OkAsync(Get(client, expiring)));
            Assert.Equal(ApprovalStatuses.Expired, e.GetProperty("status").GetString());
            Assert.Equal(ApprovalCauses.Expiry, e.GetProperty("resolution").GetProperty("cause").GetString());
            Assert.False(e.GetProperty("resolution").TryGetProperty("decidedBy", out var decider) && decider.ValueKind != JsonValueKind.Null);
        }
        finally { _clock.Offset = TimeSpan.Zero; }

        var cancelled = IdOf(await OkAsync(Request(client, [_ws.AccountId])));
        Assert.Equal(HttpStatusCode.OK, (await _factory.ApiClient(key).PostAsync($"/api/v1/approvals/{cancelled}/cancel", null)).StatusCode);
        var c = ApprovalOf(await OkAsync(Get(client, cancelled)));
        Assert.Equal(ApprovalStatuses.Cancelled, c.GetProperty("status").GetString());
        Assert.False(c.GetProperty("proceed").GetBoolean());
        // Cancelled is terminal: a human can no longer approve it.
        Assert.Equal(HttpStatusCode.Conflict, (await HumanDecidesAsync(_ws, cancelled, ApprovalDecisions.Approve)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_human_decisions_leave_one_outcome_that_mcp_reads()
    {
        await using var client = await ConnectAsync(await AgentKeyAsync());
        var id = IdOf(await OkAsync(Request(client, [_ws.AccountId, _member.AccountId])));
        var results = await Task.WhenAll(
            HumanDecidesAsync(_ws, id, ApprovalDecisions.Approve), HumanDecidesAsync(_member, id, ApprovalDecisions.Reject),
            HumanDecidesAsync(_ws, id, ApprovalDecisions.Reject), HumanDecidesAsync(_member, id, ApprovalDecisions.Approve));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));

        var winner = await results.Single(r => r.StatusCode == HttpStatusCode.OK).ReadJson<ApprovalResource>();
        var a = ApprovalOf(await OkAsync(Get(client, id)));
        Assert.Equal(winner.Status, a.GetProperty("status").GetString());
        Assert.Equal(2, a.GetProperty("version").GetInt32());
        Assert.Equal(winner.Status, Row(id).Status);
    }
}
