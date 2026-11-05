using System.Net.Http.Json;
using HashRelay.Contracts;
using HashRelay.Server.Data;
using HashRelay.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>
/// ADR-001 (architecture/decisions): a delivery queued for an offline endpoint is kept for a bounded replay window,
/// then expires whether or not the endpoint ever returns; an expired delivery is reported as expired and never as
/// delivered. Model: ::HashRelay.Notifications.HashtagDelivery.BoundedOfflineRetention.
/// </summary>
public sealed class RetentionTests : IAsyncLifetime
{
    private readonly ShiftableClock _clock = new();
    private readonly RelayFactory _factory;
    private RelayFactory.Workspace _ws = null!;

    public RetentionTests()
    {
        _factory = new RelayFactory
        {
            Settings = { ["HashRelay:Deliveries:SweepIntervalSeconds"] = "0" }, // the test drives the sweep
            TestServices = s => { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(_clock); },
        };
    }

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("Retention Co");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private async Task<PublishResult> PublishAsync(string title, Guid? id = null)
        => await (await _factory.ApiClient(_ws.ApiKey).PostAsJsonAsync("/api/v1/notifications",
            new PublishRequest(["ops"], title, NotificationId: id))).ReadJson<PublishResult>();

    private async Task<int> SweepAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RoutingService>().ExpireDueAsync(CancellationToken.None);
    }

    private async Task<List<string>> StatusesAsync(string endpointId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        return await db.Deliveries.AsNoTracking().Where(d => d.TenantId == _ws.TenantId && d.Endpoint.EndpointId == endpointId)
            .OrderBy(d => d.CreatedAt).Select(d => d.Status).ToListAsync();
    }

    [Fact]
    public async Task A_queued_delivery_expires_when_its_window_elapses_even_if_the_endpoint_never_returns()
    {
        await _factory.ApiClient(_ws.ApiKey).PutAsJsonAsync("/api/v1/endpoints/away-1/subscriptions", new SubscriptionsRequest(["ops"]));
        Assert.Equal(1, (await PublishAsync("Disk almost full")).Queued);

        // Inside the window nothing expires.
        _clock.Offset = TimeSpan.FromHours(23);
        Assert.Equal(0, await SweepAsync());
        Assert.Equal([DeliveryStatus.Queued], await StatusesAsync("away-1"));

        // Past it, the sweep expires the delivery although the endpoint has not reconnected.
        _clock.Offset = TimeSpan.FromHours(25);
        Assert.Equal(1, await SweepAsync());
        Assert.Equal([DeliveryStatus.Expired], await StatusesAsync("away-1"));
        Assert.Equal(0, await SweepAsync()); // idempotent
    }

    [Fact]
    public async Task An_expired_delivery_is_never_counted_as_delivered()
    {
        await _factory.ApiClient(_ws.ApiKey).PutAsJsonAsync("/api/v1/endpoints/away-2/subscriptions", new SubscriptionsRequest(["ops"]));
        var id = Guid.NewGuid();
        Assert.Equal(1, (await PublishAsync("Certificate renews tonight", id)).Queued);
        _clock.Offset = TimeSpan.FromHours(25);
        await SweepAsync();
        Assert.Equal([DeliveryStatus.Expired], await StatusesAsync("away-2"));

        // The activity list (API, dashboard, MCP) reports it as not delivered.
        var recent = await (await _factory.ApiClient(_ws.ApiKey).GetAsync("/api/v1/notifications")).ReadJson<List<NotificationSummary>>();
        var n = Assert.Single(recent, r => r.NotificationId == id);
        Assert.Equal(1, n.MatchedEndpoints);
        Assert.Equal(0, n.Delivered);

        // A retried publish of the same notification reports it neither delivered nor still queued.
        var again = await PublishAsync("Certificate renews tonight", id);
        Assert.True(again.Duplicate);
        Assert.Equal(0, again.DeliveredLive);
        Assert.Equal(0, again.Queued);
    }
}
