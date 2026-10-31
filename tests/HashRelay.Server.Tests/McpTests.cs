using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HashRelay.Contracts;
using HashRelay.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>Drives the MCP endpoint with the official client over the in-memory test server.</summary>
public sealed class McpTests : IAsyncLifetime
{
    private readonly RelayFactory _factory = new();
    private RelayFactory.Workspace _ws = null!;

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("MCP Co");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private async Task<string> ServiceKeyAsync(string name, params string[] scopes)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ServiceAccountService>();
        var draft = ServiceAccountService.Validate(name, null, scopes, "agent-*", null, 60);
        var account = await svc.CreateAsync(_ws.TenantId, _ws.AccountId, draft, CancellationToken.None);
        return (await svc.IssueKeyAsync(_ws.TenantId, account.Id, _ws.AccountId, null, null, CancellationToken.None)).Secret;
    }

    private async Task<McpClient> ConnectAsync(string key)
    {
        var http = _factory.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_factory.Server.BaseAddress, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
        }, http);
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task Mcp_without_a_credential_is_401()
    {
        var c = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json") };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var res = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("Bearer", res.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Mcp_rejects_browser_origins_it_does_not_know()
    {
        var key = await ServiceKeyAsync("origin-bot", ServiceScopes.Publish);
        var c = _factory.ApiClient(key);
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json") };
        req.Headers.Add("Origin", "https://evil.example");
        var res = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Tools_are_listed_and_publish_notification_delivers_through_the_normal_pipeline()
    {
        var key = await ServiceKeyAsync("claude", ServiceScopes.Publish);
        await using var client = await ConnectAsync(key);

        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "publish_notification", "list_tags", "list_notifications", "get_notification", "list_endpoints", "set_subscriptions" }, names);

        // A subscribed device so the publish has a target.
        var api = _factory.ApiClient(_ws.ApiKey);
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsJsonAsync("/api/v1/endpoints", new RegisterEndpointRequest("dev-1", "windows", "Dev box"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.PutAsync("/api/v1/endpoints/dev-1/subscriptions/agent-reports", null)).StatusCode);

        var tags = await client.CallToolAsync("list_tags", new Dictionary<string, object?>());
        Assert.NotEqual(true, tags.IsError);
        Assert.Contains("agent-reports", Text(tags));

        var published = await client.CallToolAsync("publish_notification", new Dictionary<string, object?>
        {
            ["tags"] = new[] { "#agent-reports" }, ["title"] = "Nightly summary", ["body"] = "All green", ["priority"] = "high",
        });
        Assert.NotEqual(true, published.IsError);
        using var doc = JsonDocument.Parse(Text(published));
        Assert.Equal(1, doc.RootElement.GetProperty("matchedDevices").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("queued").GetInt32());

        var recent = await api.GetFromJsonAsync<List<NotificationSummary>>("/api/v1/notifications");
        Assert.Single(recent!, n => n.Title == "Nightly summary");
    }

    [Fact]
    public async Task Tool_calls_outside_the_policy_fail_as_tool_errors_not_transport_errors()
    {
        var key = await ServiceKeyAsync("limited", ServiceScopes.Publish);
        await using var client = await ConnectAsync(key);

        var wrongTag = await client.CallToolAsync("publish_notification", new Dictionary<string, object?> { ["tags"] = new[] { "alerts" }, ["title"] = "x" });
        Assert.Equal(true, wrongTag.IsError);
        Assert.Contains("#alerts", Text(wrongTag));

        var noRead = await client.CallToolAsync("list_notifications", new Dictionary<string, object?>());
        Assert.Equal(true, noRead.IsError);
        Assert.Contains(ServiceScopes.Read, Text(noRead));
    }

    [Fact]
    public async Task Workspace_resource_describes_the_callers_limits()
    {
        var key = await ServiceKeyAsync("introspective", ServiceScopes.Publish, ServiceScopes.Read);
        await using var client = await ConnectAsync(key);
        var resources = await client.ListResourcesAsync();
        Assert.Contains(resources, r => r.Uri == "hashrelay://workspace");
        var read = await client.ReadResourceAsync("hashrelay://workspace");
        var text = read.Contents.OfType<TextResourceContents>().Single().Text;
        Assert.Contains("introspective", text);
        Assert.Contains("agent-*", text);
        Assert.Contains(ServiceScopes.Read, text);
    }
}
