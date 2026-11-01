using System.Net.Http.Json;
using HashRelay.Contracts;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>
/// Backlog semantics over the hub: what a reconnecting endpoint gets, how it is flagged, that acknowledgement stops
/// repeats, that an interrupted synchronisation resumes, and that a new subscription never replays history.
/// </summary>
public sealed class CatchUpTests : IAsyncLifetime
{
    private readonly RelayFactory _factory = new();
    private RelayFactory.Workspace _ws = null!;

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("Catch-up Co");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private sealed record Session(HubConnection Hub, HelloResponse Hello, List<NotificationMessage> Received) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Hub.DisposeAsync();
    }

    /// <summary>Connects as the endpoint and says Hello; everything the server replays during Hello is in <c>Received</c> when this returns.</summary>
    private async Task<Session> ConnectAsync(string endpointId)
    {
        var hub = _factory.Hub(_ws.ApiKey);
        var received = new List<NotificationMessage>();
        hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => { lock (received) received.Add(m); });
        await hub.StartAsync();
        var hello = await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest(endpointId, "Desk", "windows", "test"));
        await Task.Delay(150); // long-polling transport: let any trailing messages land
        return new Session(hub, hello, received);
    }

    private async Task<PublishResult> PublishAsync(string[] tags, string title, string? priority = null)
        => await (await _factory.ApiClient(_ws.ApiKey).PostAsJsonAsync("/api/v1/notifications", new PublishRequest(tags, title, Priority: priority))).ReadJson<PublishResult>();

    private async Task<List<Delivery>> DeliveriesAsync(string endpointId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        return await db.Deliveries.AsNoTracking().Where(d => d.TenantId == _ws.TenantId && d.Endpoint.EndpointId == endpointId).OrderBy(d => d.CreatedAt).ToListAsync();
    }

    [Fact]
    public async Task Offline_backlog_is_replayed_as_a_flagged_batch_and_acknowledgement_ends_it()
    {
        var api = _factory.ApiClient(_ws.ApiKey);
        await api.PutAsJsonAsync("/api/v1/endpoints/cu-1/subscriptions", new SubscriptionsRequest(["builds", "deploys"]));
        for (var i = 1; i <= 5; i++) Assert.Equal(1, (await PublishAsync(["builds"], $"Build {i}")).Queued);
        Assert.Equal(1, (await PublishAsync(["builds", "deploys"], "Both tags")).Queued); // one delivery despite two matching tags

        await using var first = await ConnectAsync("cu-1");
        Assert.Equal(6, first.Hello.PendingReplay);
        Assert.Equal(6, first.Received.Count);
        Assert.All(first.Received, m => Assert.True(m.Replayed));
        Assert.Equal(6, first.Received.Select(m => m.NotificationId).Distinct().Count());
        // Oldest first, so a client can present them in order.
        Assert.Equal(first.Received.OrderBy(m => m.PublishedAtUtc).Select(m => m.NotificationId), first.Received.Select(m => m.NotificationId));
        // Attribution travels with the replay: the key's label, with the type it was (not) declared with.
        Assert.All(first.Received, m => { Assert.Equal("full", m.Publisher?.Name); Assert.Equal(PublisherTypes.Unknown, m.Publisher?.Type); Assert.Equal(Channels.Rest, m.Publisher?.Channel); });

        // The client acknowledges receipt of each (it will show one summary, not six toasts; the server does not care which).
        foreach (var m in first.Received) await first.Hub.InvokeAsync(HubMethods.Ack, m.DeliveryId);
        await first.Hub.StopAsync();

        // Repeated reconnects neither replay nor need acknowledging again.
        await using var second = await ConnectAsync("cu-1");
        Assert.Equal(0, second.Hello.PendingReplay);
        Assert.Empty(second.Received);
        Assert.All(await DeliveriesAsync("cu-1"), d => Assert.Equal(DeliveryStatus.Acked, d.Status));
    }

    [Fact]
    public async Task Interrupted_synchronisation_resumes_on_the_next_hello_and_gives_up_after_the_attempt_limit()
    {
        var api = _factory.ApiClient(_ws.ApiKey);
        await api.PutAsJsonAsync("/api/v1/endpoints/cu-2/subscriptions", new SubscriptionsRequest(["alerts"]));
        await PublishAsync(["alerts"], "Disk full");
        await PublishAsync(["alerts"], "Disk fuller");

        // Connection 1 is handed the backlog but acknowledges nothing (crashed mid-sync, or the socket dropped before delivery).
        await using (var s1 = await ConnectAsync("cu-2")) { Assert.Equal(2, s1.Hello.PendingReplay); await s1.Hub.StopAsync(); }
        Assert.All(await DeliveriesAsync("cu-2"), d => { Assert.Equal(DeliveryStatus.Sent, d.Status); Assert.Equal(1, d.Attempts); });

        // Connection 2 gets the same two again: nothing was lost. It acknowledges one of them.
        await using (var s2 = await ConnectAsync("cu-2"))
        {
            Assert.Equal(2, s2.Hello.PendingReplay);
            Assert.All(s2.Received, m => Assert.True(m.Replayed));
            await s2.Hub.InvokeAsync(HubMethods.Ack, s2.Received.Single(m => m.Title == "Disk full").DeliveryId);
            await s2.Hub.StopAsync();
        }

        // Connection 3: only the unacknowledged one comes back (third and last attempt).
        await using (var s3 = await ConnectAsync("cu-2"))
        {
            Assert.Equal(1, s3.Hello.PendingReplay);
            Assert.Equal("Disk fuller", s3.Received.Single().Title);
            await s3.Hub.StopAsync();
        }

        // Connection 4: the attempt limit (3) is spent, so a client that never acks is not sent the same thing forever.
        await using (var s4 = await ConnectAsync("cu-2")) Assert.Equal(0, s4.Hello.PendingReplay);
        var rows = await DeliveriesAsync("cu-2");
        Assert.Equal(DeliveryStatus.Acked, rows.Single(d => d.Attempts == 2).Status);
        Assert.Equal(DeliveryStatus.Sent, rows.Single(d => d.Attempts == 3).Status);
    }

    [Fact]
    public async Task Acknowledged_live_delivery_is_never_replayed()
    {
        await using var s = await ConnectAsync("cu-3");
        await s.Hub.InvokeAsync<IReadOnlyList<string>>(HubMethods.Subscribe, new[] { "live" });
        var pub = await PublishAsync(["live"], "Right now");
        Assert.Equal(1, pub.DeliveredLive);
        await Task.Delay(200);
        var live = Assert.Single(s.Received);
        Assert.False(live.Replayed);
        await s.Hub.InvokeAsync(HubMethods.Ack, live.DeliveryId);
        await s.Hub.StopAsync();

        await using var again = await ConnectAsync("cu-3");
        Assert.Equal(0, again.Hello.PendingReplay);
        Assert.Empty(again.Received);
    }

    [Fact]
    public async Task Unacknowledged_live_delivery_is_offered_again_on_reconnect_and_flagged_as_backlog()
    {
        // A live send is "handed to the connection", not proof of receipt; an agent that died before acking gets it back as backlog.
        await using var s = await ConnectAsync("cu-4");
        await s.Hub.InvokeAsync<IReadOnlyList<string>>(HubMethods.Subscribe, new[] { "live" });
        await PublishAsync(["live"], "Lost in transit");
        await Task.Delay(200);
        Assert.Single(s.Received);
        await s.Hub.StopAsync();

        await using var again = await ConnectAsync("cu-4");
        Assert.Equal(1, again.Hello.PendingReplay);
        var replay = Assert.Single(again.Received);
        Assert.True(replay.Replayed);
        Assert.Equal(s.Received[0].NotificationId, replay.NotificationId); // same id: the client de-duplicates if it did see it
    }

    [Fact]
    public async Task New_subscription_receives_only_what_is_published_after_it_and_history_stays_readable_on_demand()
    {
        var api = _factory.ApiClient(_ws.ApiKey);
        var old = await PublishAsync(["history"], "Before anyone subscribed");
        Assert.Equal(0, old.MatchedEndpoints);

        // Subscribe while offline, through REST: no deliveries are manufactured for the past.
        await api.PutAsJsonAsync("/api/v1/endpoints/cu-5/subscriptions", new SubscriptionsRequest(["history"]));
        Assert.Empty(await DeliveriesAsync("cu-5"));
        await using var s = await ConnectAsync("cu-5");
        Assert.Equal(0, s.Hello.PendingReplay);
        Assert.Empty(s.Received);

        // Subscribing over the hub while connected does not replay either; only what comes next is delivered, live.
        await s.Hub.InvokeAsync<IReadOnlyList<string>>(HubMethods.Subscribe, new[] { "history", "more" });
        await Task.Delay(200);
        Assert.Empty(s.Received);
        var fresh = await PublishAsync(["more"], "After subscribing");
        Assert.Equal(1, fresh.DeliveredLive);
        await Task.Delay(200);
        Assert.Equal("After subscribing", Assert.Single(s.Received).Title);
        Assert.False(s.Received[0].Replayed);

        // Explicit retrieval of history is a separate operation and still works.
        var recent = await api.GetFromJsonAsync<List<NotificationSummary>>("/api/v1/notifications");
        Assert.Contains(recent!, n => n.NotificationId == old.NotificationId);
    }

    [Fact]
    public async Task Inbox_lists_the_backlog_with_publisher_and_status_regardless_of_presentation()
    {
        var api = _factory.ApiClient(_ws.ApiKey);
        await api.PutAsJsonAsync("/api/v1/endpoints/cu-6/subscriptions", new SubscriptionsRequest(["inbox"]));
        await PublishAsync(["inbox"], "Queued one", Priorities.High);
        var inbox = await api.GetFromJsonAsync<List<EndpointDeliveryInfo>>("/api/v1/endpoints/cu-6/deliveries");
        var item = Assert.Single(inbox!);
        Assert.Equal(DeliveryStatus.Queued, item.Status);
        Assert.Equal("full", item.Publisher?.Name);
        Assert.Equal(Priorities.High, item.Priority);
    }
}
