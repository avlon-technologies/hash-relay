using System.Net.Http.Json;
using HashRelay.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>End-to-end over the hub: Hello, Subscribe, live delivery, once-per-endpoint, replay and tenant isolation.</summary>
public sealed class RealtimeTests : IAsyncLifetime
{
    private readonly RelayFactory _factory = new();
    private RelayFactory.Workspace _a = null!;
    private RelayFactory.Workspace _b = null!;

    public async ValueTask InitializeAsync()
    {
        _a = await _factory.CreateWorkspaceAsync("RT A");
        _b = await _factory.CreateWorkspaceAsync("RT B");
    }
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private static Task<T> WithTimeout<T>(Task<T> t) => t.WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Hub_rejects_connection_without_credential()
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, HubMethods.Path), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            }).Build();
        await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
    }

    [Fact]
    public async Task Connected_agent_receives_live_notification_once_for_multiple_tags()
    {
        await using var hub = _factory.Hub(_a.ApiKey);
        var received = new List<NotificationMessage>();
        var first = new TaskCompletionSource<NotificationMessage>();
        hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => { received.Add(m); first.TrySetResult(m); });
        await hub.StartAsync();

        var hello = await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest("agent-1", "Desk", "windows", "test"));
        Assert.Equal(_a.TenantId, hello.Tenant.Id);
        Assert.Empty(hello.Subscriptions);

        var subs = await hub.InvokeAsync<IReadOnlyList<string>>(HubMethods.Subscribe, new[] { "#builds", "ci" });
        Assert.Equal(["builds", "ci"], subs);

        var api = _factory.ApiClient(_a.ApiKey);
        var pub = await (await api.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds", "ci"], "Build complete", "ok"))).ReadJson<PublishResult>();
        Assert.Equal(1, pub.MatchedEndpoints);
        Assert.Equal(1, pub.DeliveredLive);

        var msg = await WithTimeout(first.Task);
        Assert.Equal("Build complete", msg.Title);
        Assert.Equal(pub.NotificationId, msg.NotificationId);
        Assert.Equal(_a.TenantId, msg.TenantId);
        Assert.False(msg.Replayed);

        await Task.Delay(300);
        Assert.Single(received);

        await hub.InvokeAsync(HubMethods.Ack, msg.DeliveryId);
    }

    [Fact]
    public async Task Subscription_changes_over_rest_are_pushed_to_connected_agent()
    {
        await using var hub = _factory.Hub(_a.ApiKey);
        var changed = new TaskCompletionSource<IReadOnlyList<string>>();
        hub.On<IReadOnlyList<string>>(HubMethods.SubscriptionsChanged, t => changed.TrySetResult(t));
        await hub.StartAsync();
        await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest("agent-2", "Desk", "windows", "test"));

        var api = _factory.ApiClient(_a.ApiKey);
        await api.PostAsJsonAsync("/api/v1/endpoints/agent-2/subscriptions", new SubscriptionsRequest(["deploys"]));
        var tags = await WithTimeout(changed.Task);
        Assert.Equal(["deploys"], tags);
    }

    [Fact]
    public async Task Offline_delivery_is_replayed_on_next_hello()
    {
        var api = _factory.ApiClient(_a.ApiKey);
        await api.PutAsJsonAsync("/api/v1/endpoints/agent-3/subscriptions", new SubscriptionsRequest(["alerts"]));
        var pub = await (await api.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["alerts"], "Missed me?"))).ReadJson<PublishResult>();
        Assert.Equal(1, pub.Queued);

        await using var hub = _factory.Hub(_a.ApiKey);
        var got = new TaskCompletionSource<NotificationMessage>();
        hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => got.TrySetResult(m));
        await hub.StartAsync();
        var hello = await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest("agent-3", "Desk", "windows", "test"));
        Assert.Equal(1, hello.PendingReplay);
        var msg = await WithTimeout(got.Task);
        Assert.True(msg.Replayed);
        Assert.Equal("Missed me?", msg.Title);
        await hub.InvokeAsync(HubMethods.Ack, msg.DeliveryId);

        // Once acknowledged, reconnecting does not replay the same delivery again (unacknowledged ones are: see CatchUpTests).
        await using var hub2 = _factory.Hub(_a.ApiKey);
        await hub2.StartAsync();
        var hello2 = await hub2.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest("agent-3", "Desk", "windows", "test"));
        Assert.Equal(0, hello2.PendingReplay);
    }

    [Fact]
    public async Task Publication_in_tenant_B_never_reaches_connected_agent_in_tenant_A()
    {
        await using var hub = _factory.Hub(_a.ApiKey);
        var got = new TaskCompletionSource<NotificationMessage>();
        hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => got.TrySetResult(m));
        await hub.StartAsync();
        await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest("agent-4", "Desk", "windows", "test"));
        await hub.InvokeAsync<IReadOnlyList<string>>(HubMethods.Subscribe, new[] { "builds" });

        var b = _factory.ApiClient(_b.ApiKey);
        var pub = await (await b.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds"], "from B"))).ReadJson<PublishResult>();
        Assert.Equal(0, pub.MatchedEndpoints);

        var completed = await Task.WhenAny(got.Task, Task.Delay(500));
        Assert.NotSame(got.Task, completed);
    }

    [Fact]
    public async Task Endpoint_shows_online_while_connected()
    {
        await using var hub = _factory.Hub(_a.ApiKey);
        await hub.StartAsync();
        await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest("agent-5", "Desk", "windows", "test"));
        var api = _factory.ApiClient(_a.ApiKey);
        var ep = await (await api.GetAsync("/api/v1/endpoints/agent-5")).ReadJson<EndpointInfo>();
        Assert.True(ep.Online);
        await hub.StopAsync();
        await Task.Delay(300);
        ep = await (await api.GetAsync("/api/v1/endpoints/agent-5")).ReadJson<EndpointInfo>();
        Assert.False(ep.Online);
    }
}
