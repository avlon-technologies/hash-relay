using System.Net;
using System.Net.Http.Json;
using HashRelay.Contracts;
using HashRelay.Server.Data;
using HashRelay.Server.Services.Push;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>Mobile endpoints: push-token registration, FCM hand-off on publish, provider-acceptance evidence, HTTP ack and the inbox.</summary>
public sealed class PushTests : IAsyncLifetime
{
    private sealed class FakePushSender : IPushSender
    {
        public readonly List<PushMessage> Sent = [];
        public Func<PushMessage, PushResult> Respond = _ => new PushResult(PushOutcome.Accepted, "projects/x/messages/1", null);
        public bool Enabled => true;
        public string Provider => PushProviders.Fcm;
        public Task<PushResult> SendAsync(PushMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(Respond(message));
        }
    }

    private readonly FakePushSender _push = new();
    private readonly RelayFactory _factory;
    private RelayFactory.Workspace _ws = null!;

    public PushTests()
    {
        _factory = new RelayFactory { TestServices = s => { s.RemoveAll<IPushSender>(); s.AddSingleton<IPushSender>(_push); } };
    }

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("Mobile");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private async Task<Data.Endpoint> EndpointAsync(string endpointId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        return await db.Endpoints.AsNoTracking().FirstAsync(e => e.TenantId == _ws.TenantId && e.EndpointId == endpointId);
    }

    private async Task<Delivery> DeliveryAsync(Guid notificationId, string endpointId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        return await db.Deliveries.AsNoTracking().FirstAsync(d => d.NotificationId == notificationId && d.Endpoint.EndpointId == endpointId);
    }

    [Fact]
    public async Task Pairing_accepts_the_mobile_client_and_rejects_unknown_clients()
    {
        var anon = _factory.ApiClient(null);
        var ok = await anon.PostAsJsonAsync("/api/v1/device/code", new DeviceCodeRequest("phone-1", "Test phone", "android", "0.1.0", PairingClients.Mobile));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var bad = await anon.PostAsJsonAsync("/api/v1/device/code", new DeviceCodeRequest("phone-1", "Test phone", "android", "0.1.0", "tablet"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Registering_a_push_token_is_stored_but_never_exposed()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var res = await c.PostAsJsonAsync("/api/v1/endpoints", new RegisterEndpointRequest("phone-2", "android", "Pixel", "0.1.0", PushProviders.Fcm, "tok-abc"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var info = await res.ReadJson<EndpointInfo>();
        Assert.Equal("fcm", info.PushProvider);
        Assert.DoesNotContain("tok-abc", await res.Content.ReadAsStringAsync());
        var ep = await EndpointAsync("phone-2");
        Assert.Equal("tok-abc", ep.PushToken);

        var bad = await c.PostAsJsonAsync("/api/v1/endpoints", new RegisterEndpointRequest("phone-2", "android", null, null, "apns", "x"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // An explicit empty token clears it; null leaves it alone.
        await c.PutAsJsonAsync("/api/v1/endpoints/phone-2", new RegisterEndpointRequest("phone-2", "android"));
        Assert.Equal("tok-abc", (await EndpointAsync("phone-2")).PushToken);
        await c.PutAsJsonAsync("/api/v1/endpoints/phone-2", new RegisterEndpointRequest("phone-2", "android", null, null, PushProviders.Fcm, ""));
        Assert.Null((await EndpointAsync("phone-2")).PushToken);
    }

    [Fact]
    public async Task Same_token_moves_to_the_newest_endpoint()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        await c.PostAsJsonAsync("/api/v1/endpoints", new RegisterEndpointRequest("old-install", "ios", null, null, PushProviders.Fcm, "tok-same"));
        await c.PostAsJsonAsync("/api/v1/endpoints", new RegisterEndpointRequest("new-install", "ios", null, null, PushProviders.Fcm, "tok-same"));
        Assert.Null((await EndpointAsync("old-install")).PushToken);
        Assert.Equal("tok-same", (await EndpointAsync("new-install")).PushToken);
    }

    [Fact]
    public async Task Publish_to_an_offline_mobile_endpoint_is_pushed_and_recorded_as_provider_acceptance()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        await c.PostAsJsonAsync("/api/v1/endpoints", new RegisterEndpointRequest("phone-3", "android", "Pixel", null, PushProviders.Fcm, "tok-3"));
        await c.PutAsJsonAsync("/api/v1/endpoints/phone-3/subscriptions", new SubscriptionsRequest(["builds"]));

        var pub = await (await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds"], "Build green", "All 42 tests passed", "https://ci.example/1", Priority: "high"))).ReadJson<PublishResult>();
        Assert.Equal(1, pub.MatchedEndpoints);
        Assert.Equal(0, pub.DeliveredLive);
        Assert.Equal(1, pub.Pushed);
        Assert.Equal(0, pub.Queued);

        var msg = Assert.Single(_push.Sent);
        Assert.Equal("tok-3", msg.Token);
        Assert.Equal("Build green", msg.Title);
        Assert.Equal("All 42 tests passed", msg.Body);
        Assert.Equal(Priorities.High, msg.Priority);
        Assert.Equal(pub.NotificationId.ToString(), msg.Data["notificationId"]);
        Assert.Equal("https://ci.example/1", msg.Data["url"]);
        Assert.Equal("builds", msg.Data["tags"]);

        var d = await DeliveryAsync(pub.NotificationId, "phone-3");
        Assert.Equal(DeliveryStatus.Pushed, d.Status);
        Assert.Null(d.AckedAt);
        Assert.Equal(d.Id.ToString(), msg.Data["deliveryId"]);

        // The HTTP ack is what turns provider acceptance into display evidence.
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/v1/endpoints/phone-3/deliveries/{d.Id}/ack", null)).StatusCode);
        Assert.Equal(DeliveryStatus.Acked, (await DeliveryAsync(pub.NotificationId, "phone-3")).Status);

        var inbox = await (await c.GetAsync("/api/v1/endpoints/phone-3/deliveries")).ReadJson<List<EndpointDeliveryInfo>>();
        var item = Assert.Single(inbox);
        Assert.Equal(DeliveryStatus.Acked, item.Status);
        Assert.Equal("Build green", item.Title);
        Assert.NotNull(item.AckedAtUtc);
    }

    [Fact]
    public async Task Pushed_deliveries_are_not_replayed_on_connect_but_queued_ones_are()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        await c.PostAsJsonAsync("/api/v1/endpoints", new RegisterEndpointRequest("phone-4", "ios", null, null, PushProviders.Fcm, "tok-4"));
        await c.PutAsJsonAsync("/api/v1/endpoints/phone-4/subscriptions", new SubscriptionsRequest(["ops"]));
        await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["ops"], "pushed one"));

        _push.Respond = _ => new PushResult(PushOutcome.Failed, null, "503 UNAVAILABLE");
        var queued = await (await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["ops"], "queued one"))).ReadJson<PublishResult>();
        Assert.Equal(1, queued.Queued);
        Assert.Equal(0, queued.Pushed);

        var received = new List<NotificationMessage>();
        await using var hub = _factory.Hub(_ws.ApiKey);
        hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => received.Add(m));
        await hub.StartAsync();
        var hello = await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest("phone-4", "iPhone", "ios", "0.1.0", PushProviders.Fcm, "tok-4-rotated"));
        Assert.Equal(1, hello.PendingReplay);
        Assert.Equal("tok-4-rotated", (await EndpointAsync("phone-4")).PushToken);
        await Task.Delay(300);
        Assert.Equal(["queued one"], received.Select(m => m.Title));
    }

    [Fact]
    public async Task Connected_mobile_endpoint_gets_live_delivery_instead_of_a_push()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        await c.PutAsJsonAsync("/api/v1/endpoints/phone-5/subscriptions", new SubscriptionsRequest(["live"]));
        var received = new TaskCompletionSource<NotificationMessage>();
        await using var hub = _factory.Hub(_ws.ApiKey);
        hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => received.TrySetResult(m));
        await hub.StartAsync();
        await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest("phone-5", "Pixel", "android", "0.1.0", PushProviders.Fcm, "tok-5"));

        var pub = await (await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["live"], "foreground"))).ReadJson<PublishResult>();
        Assert.Equal(1, pub.DeliveredLive);
        Assert.Equal(0, pub.Pushed);
        Assert.Empty(_push.Sent);
        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("foreground", msg.Title);
    }

    [Fact]
    public async Task Unregistered_token_is_dropped_and_the_delivery_stays_queued()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        await c.PostAsJsonAsync("/api/v1/endpoints", new RegisterEndpointRequest("phone-6", "android", null, null, PushProviders.Fcm, "tok-6"));
        await c.PutAsJsonAsync("/api/v1/endpoints/phone-6/subscriptions", new SubscriptionsRequest(["x"]));
        _push.Respond = _ => new PushResult(PushOutcome.InvalidToken, null, "404 NOT_FOUND: UNREGISTERED");

        var pub = await (await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["x"], "gone"))).ReadJson<PublishResult>();
        Assert.Equal(0, pub.Pushed);
        Assert.Equal(1, pub.Queued);
        var ep = await EndpointAsync("phone-6");
        Assert.Null(ep.PushToken);
        Assert.NotNull(ep.PushInvalidatedAt);
        Assert.Equal(DeliveryStatus.Queued, (await DeliveryAsync(pub.NotificationId, "phone-6")).Status);
        Assert.Null((await (await c.GetAsync("/api/v1/endpoints/phone-6")).ReadJson<EndpointInfo>()).PushProvider);

        // A fresh token re-enables push and clears the invalidation marker.
        _push.Respond = _ => new PushResult(PushOutcome.Accepted, "m", null);
        await c.PutAsJsonAsync("/api/v1/endpoints/phone-6", new RegisterEndpointRequest("phone-6", "android", null, null, PushProviders.Fcm, "tok-6b"));
        ep = await EndpointAsync("phone-6");
        Assert.Equal("tok-6b", ep.PushToken);
        Assert.Null(ep.PushInvalidatedAt);
    }

    [Fact]
    public async Task Device_token_cannot_ack_or_list_another_endpoints_deliveries()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        await c.PutAsJsonAsync("/api/v1/endpoints/phone-7/subscriptions", new SubscriptionsRequest(["t"]));
        await c.PutAsJsonAsync("/api/v1/endpoints/phone-8/subscriptions", new SubscriptionsRequest(["t"]));
        var pub = await (await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["t"], "both"))).ReadJson<PublishResult>();
        var other = await DeliveryAsync(pub.NotificationId, "phone-8");

        string deviceToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var creds = scope.ServiceProvider.GetRequiredService<Services.CredentialService>();
            var ep7 = await EndpointAsync("phone-7");
            (_, deviceToken) = await creds.IssueDeviceTokenAsync(_ws.TenantId, _ws.AccountId, ep7.Id, "phone-7", [Scopes.Subscribe], CancellationToken.None);
        }
        var dev = _factory.ApiClient(deviceToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.GetAsync("/api/v1/endpoints/phone-8/deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.PostAsync($"/api/v1/endpoints/phone-8/deliveries/{other.Id}/ack", null)).StatusCode);
        // Acking a delivery id that belongs to another endpoint through its own endpoint is a no-op.
        Assert.Equal(HttpStatusCode.NoContent, (await dev.PostAsync($"/api/v1/endpoints/phone-7/deliveries/{other.Id}/ack", null)).StatusCode);
        Assert.Equal(DeliveryStatus.Queued, (await DeliveryAsync(pub.NotificationId, "phone-8")).Status);
    }
}
