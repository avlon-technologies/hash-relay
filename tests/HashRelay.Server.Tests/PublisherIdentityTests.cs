using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HashRelay.Contracts;
using HashRelay.Server.Data;
using HashRelay.Server.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>
/// Publisher attribution is established by the server from the credential: declared types are honoured, undeclared ones
/// are unknown, MCP is not read as "agent", the request body cannot assert an identity, and legacy rows read as unknown.
/// </summary>
public sealed class PublisherIdentityTests : IAsyncLifetime
{
    private readonly RelayFactory _factory = new();
    private RelayFactory.Workspace _ws = null!;

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("Attribution Ltd");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private async Task<NotificationSummary> SummaryOf(Guid id)
    {
        var list = await _factory.ApiClient(_ws.ApiKey).GetFromJsonAsync<List<NotificationSummary>>("/api/v1/notifications?limit=50");
        return list!.Single(n => n.NotificationId == id);
    }

    private async Task<string> ApiKeyAsync(string label, string? publisherType)
    {
        using var scope = _factory.Services.CreateScope();
        var creds = scope.ServiceProvider.GetRequiredService<CredentialService>();
        return (await creds.IssueApiKeyAsync(_ws.TenantId, _ws.AccountId, label, [Scopes.Publish], null, CancellationToken.None, publisherType)).Secret;
    }

    private async Task<(ServiceAccount Account, string Key)> ServiceAccountAsync(string name, string? publisherType)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ServiceAccountService>();
        var draft = ServiceAccountService.Validate(name, null, [ServiceScopes.Publish], "*", null, 60, publisherType);
        var account = await svc.CreateAsync(_ws.TenantId, _ws.AccountId, draft, CancellationToken.None);
        return (account, (await svc.IssueKeyAsync(_ws.TenantId, account.Id, _ws.AccountId, null, null, CancellationToken.None)).Secret);
    }

    private async Task<McpClient> McpAsync(string key)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_factory.Server.BaseAddress, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
        }, _factory.CreateClient());
        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task Api_key_publishes_as_its_label_with_the_type_declared_at_creation()
    {
        var declared = _factory.ApiClient(await ApiKeyAsync("CI pipeline", PublisherTypes.Service));
        var r = await (await declared.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds"], "Build green"))).ReadJson<PublishResult>();
        var n = await SummaryOf(r.NotificationId);
        Assert.Equal("CI pipeline", n.Publisher?.Name);
        Assert.Equal(PublisherTypes.Service, n.Publisher?.Type);
        Assert.Equal(Channels.Rest, n.Publisher?.Channel);
        Assert.NotNull(n.Publisher?.Id);

        // A key created without a declaration (every pre-existing key) is unknown, not guessed.
        var legacyKey = _factory.ApiClient(_ws.ApiKey);
        var r2 = await (await legacyKey.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds"], "Older key"))).ReadJson<PublishResult>();
        var n2 = await SummaryOf(r2.NotificationId);
        Assert.Equal("full", n2.Publisher?.Name);
        Assert.Equal(PublisherTypes.Unknown, n2.Publisher?.Type);
    }

    [Fact]
    public async Task Unknown_declared_types_are_stored_as_unknown()
    {
        var c = _factory.ApiClient(await ApiKeyAsync("odd", "superintelligence"));
        var r = await (await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["x"], "t"))).ReadJson<PublishResult>();
        Assert.Equal(PublisherTypes.Unknown, (await SummaryOf(r.NotificationId)).Publisher?.Type);
    }

    [Fact]
    public async Task Client_supplied_publisher_fields_in_the_body_are_ignored()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var body = """{"tags":["builds"],"title":"Spoof attempt","publisher":{"id":"00000000-0000-0000-0000-000000000001","name":"Mallory","type":"human","channel":"cli"},"publisherName":"Mallory","publisherType":"human"}""";
        var res = await c.PostAsync("/api/v1/notifications", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var r = await res.ReadJson<PublishResult>();
        var n = await SummaryOf(r.NotificationId);
        Assert.Equal("full", n.Publisher?.Name);
        Assert.Equal(PublisherTypes.Unknown, n.Publisher?.Type);
        Assert.Equal(Channels.Rest, n.Publisher?.Channel);
    }

    [Fact]
    public async Task Mcp_publisher_is_the_service_account_and_is_not_assumed_to_be_an_agent()
    {
        var (undeclared, key1) = await ServiceAccountAsync("helper", null);
        await using (var mcp = await McpAsync(key1))
        {
            var result = await mcp.CallToolAsync("publish_notification", new Dictionary<string, object?> { ["tags"] = new[] { "agent-reports" }, ["title"] = "Via MCP" });
            Assert.NotEqual(true, result.IsError);
            using var doc = JsonDocument.Parse(string.Join("", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
            var n = await SummaryOf(doc.RootElement.GetProperty("notificationId").GetGuid());
            Assert.Equal("helper", n.Publisher?.Name);
            Assert.Equal(undeclared.Id, n.Publisher?.Id);
            Assert.Equal(PublisherTypes.Unknown, n.Publisher?.Type);   // MCP ≠ agent
            Assert.Equal(Channels.Mcp, n.Publisher?.Channel);         // but the entry point is recorded
        }

        var (declared, key2) = await ServiceAccountAsync("Claude Code", PublisherTypes.Agent);
        await using (var mcp = await McpAsync(key2))
        {
            var result = await mcp.CallToolAsync("publish_notification", new Dictionary<string, object?> { ["tags"] = new[] { "agent-reports" }, ["title"] = "Build completed" });
            using var doc = JsonDocument.Parse(string.Join("", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
            var n = await SummaryOf(doc.RootElement.GetProperty("notificationId").GetGuid());
            Assert.Equal("Claude Code", n.Publisher?.Name);
            Assert.Equal(PublisherTypes.Agent, n.Publisher?.Type);
            Assert.Equal(declared.Id, n.Publisher?.Id);
        }

        // The same service account over REST keeps its identity; only the channel differs.
        var rest = _factory.ApiClient(key2);
        var r = await (await rest.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["agent-reports"], "Via REST"))).ReadJson<PublishResult>();
        var viaRest = await SummaryOf(r.NotificationId);
        Assert.Equal(declared.Id, viaRest.Publisher?.Id);
        Assert.Equal(PublisherTypes.Agent, viaRest.Publisher?.Type);
        Assert.Equal(Channels.Rest, viaRest.Publisher?.Channel);
    }

    [Fact]
    public async Task Paired_device_publishes_as_a_person_named_after_the_device()
    {
        var anon = _factory.ApiClient(null);
        var code = await (await anon.PostAsJsonAsync("/api/v1/device/code", new DeviceCodeRequest("laptop-7", "Mike's laptop", "windows", "1.0", "cli"))).ReadJson<DeviceCodeResponse>();
        using (var scope = _factory.Services.CreateScope())
        {
            var pairing = scope.ServiceProvider.GetRequiredService<DevicePairingService>();
            var p = await pairing.FindPendingAsync(code.UserCode, CancellationToken.None);
            await pairing.ApproveAsync(p!.Id, _ws.AccountId, _ws.TenantId, [Scopes.Subscribe, Scopes.Publish], CancellationToken.None);
        }
        var token = (await (await anon.PostAsJsonAsync("/api/v1/device/token", new DeviceTokenRequest(code.DeviceCode))).ReadJson<DeviceTokenResponse>()).Token!;

        var device = _factory.ApiClient(token);
        device.DefaultRequestHeaders.UserAgent.ParseAdd("hashrelay-cli/1.0.0 (windows)");
        var r = await (await device.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds"], "Typed by hand"))).ReadJson<PublishResult>();
        var n = await SummaryOf(r.NotificationId);
        Assert.Equal("Mike's laptop (CLI)", n.Publisher?.Name);
        Assert.Equal(PublisherTypes.Human, n.Publisher?.Type);
        Assert.Equal(Channels.Cli, n.Publisher?.Channel);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var ep = await db.Endpoints.SingleAsync(e => e.TenantId == _ws.TenantId && e.EndpointId == "laptop-7");
            Assert.Equal(ep.Id, n.Publisher?.Id); // stable across re-pairing: the endpoint, not the token
        }
    }

    [Fact]
    public async Task Channel_is_the_entry_point_not_an_identity()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("hashrelay-mobile/1.0.0 (android)");
        var r = await (await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["x"], "from phone"))).ReadJson<PublishResult>();
        Assert.Equal(Channels.Mobile, (await SummaryOf(r.NotificationId)).Publisher?.Channel);

        var curl = _factory.ApiClient(_ws.ApiKey);
        curl.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.4.0");
        var r2 = await (await curl.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["x"], "from curl"))).ReadJson<PublishResult>();
        var n2 = await SummaryOf(r2.NotificationId);
        Assert.Equal(Channels.Rest, n2.Publisher?.Channel);
        Assert.Equal("full", n2.Publisher?.Name); // the User-Agent changed nothing about who published
    }

    [Fact]
    public async Task Legacy_notifications_without_attribution_read_as_unknown_everywhere()
    {
        var api = _factory.ApiClient(_ws.ApiKey);
        await api.PutAsJsonAsync("/api/v1/endpoints/legacy-1/subscriptions", new SubscriptionsRequest(["old"]));
        Guid id;
        using (var scope = _factory.Services.CreateScope())
        {
            // A row as an earlier server version wrote it: credential reference only, no publisher columns.
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            var ep = await db.Endpoints.SingleAsync(e => e.TenantId == _ws.TenantId && e.EndpointId == "legacy-1");
            id = Guid.NewGuid();
            db.Notifications.Add(new Notification
            {
                Id = id, TenantId = _ws.TenantId, TagsCsv = "old", Title = "From before", Priority = Priorities.Normal, PayloadHash = "x",
                PublishedByCredentialId = Guid.NewGuid(), PublishedAt = DateTimeOffset.UtcNow.AddMinutes(-5), MatchedEndpoints = 1,
            });
            db.Deliveries.Add(new Delivery { Id = Guid.NewGuid(), TenantId = _ws.TenantId, NotificationId = id, EndpointRef = ep.Id, Status = DeliveryStatus.Queued, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
            await db.SaveChangesAsync();
        }

        var summary = (await api.GetFromJsonAsync<List<NotificationSummary>>("/api/v1/notifications"))!.Single(n => n.NotificationId == id);
        Assert.NotNull(summary.Publisher);
        Assert.Null(summary.Publisher!.Name);
        Assert.Equal(PublisherTypes.Unknown, summary.Publisher.Type);
        Assert.False(summary.Publisher.IsKnown);

        var inbox = (await api.GetFromJsonAsync<List<EndpointDeliveryInfo>>("/api/v1/endpoints/legacy-1/deliveries"))!.Single();
        Assert.Equal(PublisherTypes.Unknown, inbox.Publisher?.Type);

        await using var hub = _factory.Hub(_ws.ApiKey);
        var got = new TaskCompletionSource<NotificationMessage>();
        hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => got.TrySetResult(m));
        await hub.StartAsync();
        await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest("legacy-1", "Desk", "windows", "test"));
        var replayed = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("From before", replayed.Title);
        Assert.Equal(PublisherTypes.Unknown, replayed.Publisher?.Type);
        Assert.Null(replayed.Publisher?.Name);
    }

    [Fact]
    public async Task OAuth_consent_records_the_declared_type_and_never_infers_one()
    {
        using var scope = _factory.Services.CreateScope();
        var oauth = scope.ServiceProvider.GetRequiredService<OAuthService>();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var account = await db.Accounts.SingleAsync(a => a.Id == _ws.AccountId);
        var (client, _) = await oauth.RegisterAsync(new OAuthService.ClientRegistration("Claude", ["https://example.test/cb"], null, null, null), CancellationToken.None);
        const string challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        await oauth.ApproveAsync(_ws.TenantId, account, client, "https://example.test/cb", [ServiceScopes.Publish], "*", 60, challenge, null, CancellationToken.None);
        var grant = await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.ClientId == client.Id);
        var sa = await db.ServiceAccounts.AsNoTracking().SingleAsync(s => s.Id == grant.ServiceAccountId);
        Assert.Equal(PublisherTypes.Unknown, PublisherTypes.Normalize(sa.PublisherType)); // undeclared → unknown, even though the client is an "MCP app"

        await oauth.ApproveAsync(_ws.TenantId, account, client, "https://example.test/cb", [ServiceScopes.Publish], "*", 60, challenge, null, CancellationToken.None, PublisherTypes.Agent);
        db.ChangeTracker.Clear();
        sa = await db.ServiceAccounts.AsNoTracking().SingleAsync(s => s.Id == grant.ServiceAccountId);
        Assert.Equal(PublisherTypes.Agent, sa.PublisherType);

        // Re-consenting without stating a type keeps what was declared rather than resetting it.
        await oauth.ApproveAsync(_ws.TenantId, account, client, "https://example.test/cb", [ServiceScopes.Publish], "*", 60, challenge, null, CancellationToken.None);
        db.ChangeTracker.Clear();
        sa = await db.ServiceAccounts.AsNoTracking().SingleAsync(s => s.Id == grant.ServiceAccountId);
        Assert.Equal(PublisherTypes.Agent, sa.PublisherType);
    }

    [Fact]
    public async Task Existing_credentials_and_clients_keep_working_and_me_is_unchanged()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var me = await c.GetFromJsonAsync<MeResponse>("/api/v1/me");
        Assert.Equal(CredentialKinds.Api, me!.Credential.Kind);
        Assert.Equal("full", me.Credential.Label);
        // Old clients deserialise NotificationMessage without the publisher field; the field is optional on the wire.
        var json = JsonSerializer.Serialize(new NotificationMessage(Guid.NewGuid(), _ws.TenantId, ["a"], "t", null, DateTimeOffset.UtcNow, Guid.NewGuid(), null, "normal", "T", false), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"publisher\":null", json);
        var withoutField = JsonSerializer.Deserialize<NotificationMessage>("""{"notificationId":"00000000-0000-0000-0000-000000000001","tenantId":"00000000-0000-0000-0000-000000000002","tags":["a"],"title":"t","body":null,"publishedAtUtc":"2026-01-01T00:00:00Z","deliveryId":"00000000-0000-0000-0000-000000000003","url":null,"priority":"normal","tenantName":"T","replayed":false}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Null(withoutField!.Publisher);
    }
}
