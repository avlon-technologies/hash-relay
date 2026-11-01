using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HashRelay.Client;
using HashRelay.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>The toast-button path: which notifications get Approve/Reject, and what those buttons lead to.</summary>
public sealed class ApprovalLinkTests
{
    private static readonly Uri Server = new("https://hashrelay.example/");
    private static NotificationMessage Msg(string? url) => new(Guid.NewGuid(), Guid.Empty, ["test"], "t", null, DateTimeOffset.UtcNow, Guid.NewGuid(), url, "high", "Acme", false);

    [Fact]
    public void Only_approval_pages_on_the_agents_own_server_get_buttons()
    {
        var id = Guid.NewGuid();
        Assert.True(ApprovalLink.TryParse(Msg($"https://hashrelay.example/approvals/{id}"), Server, out var page));
        Assert.Equal($"https://hashrelay.example/approvals/{id}", page.ToString());
        Assert.Equal($"https://hashrelay.example/approvals/{id}?decision=reject", ApprovalLink.WithDecision(page, ApprovalDecisions.Reject).ToString());

        Assert.False(ApprovalLink.TryParse(Msg($"https://evil.example/approvals/{id}"), Server, out _));
        Assert.False(ApprovalLink.TryParse(Msg($"http://hashrelay.example/approvals/{id}"), Server, out _));
        Assert.False(ApprovalLink.TryParse(Msg($"https://hashrelay.example:8443/approvals/{id}"), Server, out _));
        Assert.False(ApprovalLink.TryParse(Msg("https://hashrelay.example/approvals/not-a-guid"), Server, out _));
        Assert.False(ApprovalLink.TryParse(Msg($"https://hashrelay.example/approvals/{id}/extra"), Server, out _));
        Assert.False(ApprovalLink.TryParse(Msg("https://hashrelay.example/n/abc"), Server, out _));
        Assert.False(ApprovalLink.TryParse(Msg(null), Server, out _));
    }
}

public sealed partial class McpApprovalTests
{
    private static readonly Uri TestServer = new("http://localhost/");

    private static Task<CallToolResult> PublishApproval(McpClient c, string title = "Deployment Approval Required", int? seconds = 300, string? notificationId = null, string[]? approvers = null)
        => c.CallToolAsync("publish_notification", new Dictionary<string, object?>
        {
            ["tags"] = new[] { "test" }, ["title"] = title, ["body"] = "Deploy version 1.2.3 to production?", ["notificationId"] = notificationId,
            ["approval"] = new Dictionary<string, object?> { ["expiresInSeconds"] = seconds, ["targetUserIds"] = approvers },
        }).AsTask();

    private async Task<HubConnection> OnlineAgentAsync(string endpointId, TaskCompletionSource<NotificationMessage> got)
    {
        var hub = _factory.Hub(_ws.ApiKey);
        hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => got.TrySetResult(m));
        await hub.StartAsync();
        await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest(endpointId, "AVLON", "windows", "test"));
        await hub.InvokeAsync<IReadOnlyList<string>>(HubMethods.Subscribe, new[] { "test" });
        return hub;
    }

    private static async Task<T> Within<T>(Task<T> t) => await t.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>What a toast button does: open the page with the choice pre-selected, then the person confirms by posting the form.</summary>
    private async Task<(string Page, HttpResponseMessage Confirm)> ClickAndConfirmAsync(RelayFactory.Workspace who, Uri buttonUrl, string handler, string? key = null)
    {
        var browser = await _factory.LoginAsync(who);
        var page = await browser.GetStringAsync(buttonUrl.PathAndQuery);
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(page),
            ["Key"] = key ?? Regex.Match(page, "name=\"Key\" value=\"([^\"]+)\"").Groups[1].Value,
            ["Note"] = "",
        };
        var confirm = await browser.PostAsync($"{buttonUrl.AbsolutePath}?handler={handler}", new FormUrlEncodedContent(form));
        return (page, confirm);
    }

    [Fact]
    public async Task Publish_notification_with_approval_reaches_an_online_agent_as_an_actionable_toast_and_the_click_is_recorded()
    {
        var got = new TaskCompletionSource<NotificationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = await OnlineAgentAsync("avlon", got);
        await using var client = await ConnectAsync(await AgentKeyAsync());

        // No approvers named: the person who created this credential is asked.
        var r = await OkAsync(PublishApproval(client));
        var approvalId = r.GetProperty("approvalId").GetGuid();
        Assert.Equal(ApprovalStatuses.Pending, r.GetProperty("approvalStatus").GetString());
        Assert.Equal(1, r.GetProperty("matchedDevices").GetInt32());
        Assert.Contains("get_approval", r.GetProperty("summary").GetString());
        Assert.Equal([_ws.AccountId], Row(approvalId).ApproverIds);
        Assert.InRange(Row(approvalId).ExpiresAt - Row(approvalId).CreatedAt, TimeSpan.FromSeconds(299), TimeSpan.FromSeconds(301));

        // The live message carries the approval link the agent turns into Approve/Reject buttons.
        var msg = await Within(got.Task);
        Assert.Equal(r.GetProperty("notificationId").GetGuid(), msg.NotificationId);
        Assert.StartsWith("Approval requested: Deployment Approval Required", msg.Title);
        Assert.True(ApprovalLink.TryParse(msg, TestServer, out var page));
        Assert.Equal(r.GetProperty("approvalUrl").GetString(), page.ToString());

        // "Approve…" opens the page with approve pre-selected: nothing is recorded by opening it.
        var (html, confirm) = await ClickAndConfirmAsync(_ws, ApprovalLink.WithDecision(page, ApprovalDecisions.Approve), "Approve");
        Assert.Contains("Confirm approval", html);
        Assert.Contains("Nothing has been recorded yet", html);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Contains("You approved this request.", await confirm.Content.ReadAsStringAsync());

        var state = ApprovalOf(await OkAsync(Get(client, approvalId)));
        Assert.Equal(ApprovalStatuses.Approved, state.GetProperty("status").GetString());
        Assert.True(state.GetProperty("proceed").GetBoolean());
        Assert.Equal(_ws.AccountId, state.GetProperty("resolution").GetProperty("decidedBy").GetGuid());
    }

    [Fact]
    public async Task Opening_a_button_link_records_nothing_and_reject_is_recorded_once_even_if_submitted_twice()
    {
        await using var client = await ConnectAsync(await AgentKeyAsync());
        var r = await OkAsync(PublishApproval(client));
        var id = r.GetProperty("approvalId").GetGuid();
        var page = new Uri(r.GetProperty("approvalUrl").GetString()!);

        var browser = await _factory.LoginAsync(_ws);
        var html = await browser.GetStringAsync(ApprovalLink.WithDecision(page, ApprovalDecisions.Reject).PathAndQuery);
        Assert.Contains("Confirm rejection", html);
        Assert.Equal(ApprovalStatuses.Pending, Row(id).Status);

        // A double click submits the same form (same per-render key) twice: one decision, same answer.
        var key = Regex.Match(html, "name=\"Key\" value=\"([^\"]+)\"").Groups[1].Value;
        FormUrlEncodedContent Form() => new(new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(html), ["Key"] = key, ["Note"] = "" });
        var first = await browser.PostAsync($"{page.AbsolutePath}?handler=Reject", Form());
        var second = await browser.PostAsync($"{page.AbsolutePath}?handler=Reject", Form());
        Assert.Contains("You rejected this request.", await first.Content.ReadAsStringAsync());
        Assert.Contains("You rejected this request.", await second.Content.ReadAsStringAsync());
        Assert.Equal(2, Row(id).Version);

        // A conflicting decision afterwards changes nothing.
        var (_, approve) = await ClickAndConfirmAsync(_ws, ApprovalLink.WithDecision(page, ApprovalDecisions.Approve), "Approve");
        Assert.Contains("already resolved", await approve.Content.ReadAsStringAsync());
        var a = ApprovalOf(await OkAsync(Get(client, id)));
        Assert.Equal(ApprovalStatuses.Rejected, a.GetProperty("status").GetString());
        Assert.False(a.GetProperty("proceed").GetBoolean());
    }

    [Fact]
    public async Task An_offline_agent_gets_the_actionable_approval_on_reconnect_and_an_expired_one_cannot_be_approved()
    {
        // The endpoint exists and subscribes to #test but is offline when the approval is published.
        var first = new TaskCompletionSource<NotificationMessage>();
        await (await OnlineAgentAsync("laptop", first)).DisposeAsync();
        await using var client = await ConnectAsync(await AgentKeyAsync());
        var r = await OkAsync(PublishApproval(client, seconds: 120));
        Assert.Equal(1, r.GetProperty("queued").GetInt32());
        var id = r.GetProperty("approvalId").GetGuid();

        // Agent restarts and reconnects: the approval is replayed with its link intact.
        var replay = new TaskCompletionSource<NotificationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = _factory.Hub(_ws.ApiKey);
        hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => replay.TrySetResult(m));
        await hub.StartAsync();
        Assert.Equal(1, (await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest("laptop", "AVLON", "windows", "test"))).PendingReplay);
        var msg = await Within(replay.Task);
        Assert.True(msg.Replayed);
        Assert.True(ApprovalLink.TryParse(msg, TestServer, out var page));

        // Past expiry the page offers no decision, the API refuses one, and MCP reports Expired.
        _clock.Offset = TimeSpan.FromMinutes(3);
        try
        {
            var browser = await _factory.LoginAsync(_ws);
            var html = await browser.GetStringAsync(ApprovalLink.WithDecision(page, ApprovalDecisions.Approve).PathAndQuery);
            Assert.DoesNotContain("Confirm approval", html);
            Assert.Contains("expired", html);
            var e = ApprovalOf(await OkAsync(Get(client, id)));
            Assert.Equal(ApprovalStatuses.Expired, e.GetProperty("status").GetString());
            Assert.False(e.GetProperty("proceed").GetBoolean());
        }
        finally { _clock.Offset = TimeSpan.Zero; }
    }

    [Fact]
    public async Task Publish_notification_approval_is_idempotent_scoped_and_leaves_plain_publishing_unchanged()
    {
        await using var client = await ConnectAsync(await AgentKeyAsync());
        var nid = Guid.NewGuid().ToString();
        var a1 = await OkAsync(PublishApproval(client, notificationId: nid));
        var a2 = await OkAsync(PublishApproval(client, notificationId: nid));
        Assert.Equal(a1.GetProperty("approvalId").GetGuid(), a2.GetProperty("approvalId").GetGuid());
        Assert.True(a2.GetProperty("duplicate").GetBoolean());

        // Without approvals:create the approval option is refused; plain publish still works for that account.
        await using (var publisher = await ConnectAsync(await ServiceKeyAsync(_ws, "publisher", "test", ServiceScopes.Publish)))
        {
            Assert.Contains(ServiceScopes.ApprovalsCreate, await FailAsync(PublishApproval(publisher)));
            var plain = await OkAsync(publisher.CallToolAsync("publish_notification", new Dictionary<string, object?> { ["tags"] = new[] { "test" }, ["title"] = "Plain" }).AsTask());
            Assert.False(plain.TryGetProperty("approvalId", out var none) && none.ValueKind != JsonValueKind.Null);
        }

        // An API key has no connecting person, so approvers must be named.
        await using (var apiKey = await ConnectAsync(_ws.ApiKey))
        {
            Assert.Contains("targetUserIds", await FailAsync(PublishApproval(apiKey)));
            var named = await OkAsync(PublishApproval(apiKey, approvers: [_member.AccountId.ToString()]));
            Assert.Equal([_member.AccountId], Row(named.GetProperty("approvalId").GetGuid()).ApproverIds);
        }

        // Expiry is bounded by the service, not by the tool.
        Assert.Contains("expiresAt", await FailAsync(PublishApproval(client, seconds: 5)));
    }
}
