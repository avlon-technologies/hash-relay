using System.Net;
using System.Net.Http.Json;
using HashRelay.Contracts;
using HashRelay.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>Subscription matching, once-per-endpoint and tenant isolation, over the REST surface.</summary>
public sealed class RoutingTests : IAsyncLifetime
{
    private readonly RelayFactory _factory = new();
    private RelayFactory.Workspace _a = null!;
    private RelayFactory.Workspace _b = null!;

    public async ValueTask InitializeAsync()
    {
        _a = await _factory.CreateWorkspaceAsync("Tenant A");
        _b = await _factory.CreateWorkspaceAsync("Tenant B");
    }
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private async Task<int> DeliveriesFor(Guid tenantId, string endpointId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        return await db.Deliveries.CountAsync(d => d.TenantId == tenantId && d.Endpoint.EndpointId == endpointId);
    }

    [Fact]
    public async Task Subscribe_then_publish_matches_and_records_one_queued_delivery()
    {
        var c = _factory.ApiClient(_a.ApiKey);
        var sub = await c.PutAsJsonAsync("/api/v1/endpoints/dev-1/subscriptions", new SubscriptionsRequest(["#Builds"]));
        Assert.Equal(HttpStatusCode.OK, sub.StatusCode);
        Assert.Equal(["builds"], (await sub.ReadJson<SubscriptionsResponse>()).Tags);

        var pub = await (await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds"], "Build complete"))).ReadJson<PublishResult>();
        Assert.Equal(1, pub.MatchedEndpoints);
        Assert.Equal(0, pub.DeliveredLive);   // not connected
        Assert.Equal(1, pub.Queued);
        Assert.Equal(1, await DeliveriesFor(_a.TenantId, "dev-1"));
    }

    [Fact]
    public async Task Publish_to_other_tag_does_not_match()
    {
        var c = _factory.ApiClient(_a.ApiKey);
        await c.PutAsJsonAsync("/api/v1/endpoints/dev-2/subscriptions", new SubscriptionsRequest(["builds"]));
        var pub = await (await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["deploys"], "x"))).ReadJson<PublishResult>();
        Assert.Equal(0, pub.MatchedEndpoints);
        Assert.Equal(0, await DeliveriesFor(_a.TenantId, "dev-2"));
    }

    [Fact]
    public async Task Multiple_matching_tags_yield_exactly_one_delivery_per_endpoint()
    {
        var c = _factory.ApiClient(_a.ApiKey);
        await c.PutAsJsonAsync("/api/v1/endpoints/dev-3/subscriptions", new SubscriptionsRequest(["builds", "ci", "alerts"]));
        await c.PutAsJsonAsync("/api/v1/endpoints/dev-4/subscriptions", new SubscriptionsRequest(["ci"]));
        var pub = await (await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds", "ci", "alerts"], "x"))).ReadJson<PublishResult>();
        Assert.Equal(2, pub.MatchedEndpoints);
        Assert.Equal(1, await DeliveriesFor(_a.TenantId, "dev-3"));
        Assert.Equal(1, await DeliveriesFor(_a.TenantId, "dev-4"));
    }

    [Fact]
    public async Task Tenant_B_cannot_publish_into_tenant_A_subscription()
    {
        var a = _factory.ApiClient(_a.ApiKey);
        var b = _factory.ApiClient(_b.ApiKey);
        await a.PutAsJsonAsync("/api/v1/endpoints/shared-name/subscriptions", new SubscriptionsRequest(["builds"]));

        var pub = await (await b.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds"], "intruder"))).ReadJson<PublishResult>();
        Assert.Equal(0, pub.MatchedEndpoints);
        Assert.Equal(0, await DeliveriesFor(_a.TenantId, "shared-name"));
    }

    [Fact]
    public async Task Tenant_B_cannot_read_or_change_tenant_A_endpoints()
    {
        var a = _factory.ApiClient(_a.ApiKey);
        var b = _factory.ApiClient(_b.ApiKey);
        await a.PutAsJsonAsync("/api/v1/endpoints/a-only/subscriptions", new SubscriptionsRequest(["secret"]));

        var list = await (await b.GetAsync("/api/v1/endpoints")).ReadJson<List<EndpointInfo>>();
        Assert.DoesNotContain(list, e => e.EndpointId == "a-only");

        // Same endpointId in tenant B is a *different* endpoint: B creates its own, A's subscriptions are untouched.
        await b.PutAsJsonAsync("/api/v1/endpoints/a-only/subscriptions", new SubscriptionsRequest(["other"]));
        var aSubs = await (await a.GetAsync("/api/v1/endpoints/a-only/subscriptions")).ReadJson<SubscriptionsResponse>();
        Assert.Equal(["secret"], aSubs.Tags);

        var notifications = await (await b.GetAsync("/api/v1/notifications")).ReadJson<List<NotificationSummary>>();
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Single_tag_routes_are_idempotent_204()
    {
        var c = _factory.ApiClient(_a.ApiKey);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsync("/api/v1/endpoints/dev-5/subscriptions/%23demo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsync("/api/v1/endpoints/dev-5/subscriptions/demo", null)).StatusCode);
        var subs = await (await c.GetAsync("/api/v1/endpoints/dev-5/subscriptions")).ReadJson<SubscriptionsResponse>();
        Assert.Equal(["demo"], subs.Tags);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/v1/endpoints/dev-5/subscriptions/demo")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/v1/endpoints/dev-5/subscriptions/demo")).StatusCode);
        subs = await (await c.GetAsync("/api/v1/endpoints/dev-5/subscriptions")).ReadJson<SubscriptionsResponse>();
        Assert.Empty(subs.Tags);
    }

    [Fact]
    public async Task Register_endpoint_returns_201_then_200()
    {
        var c = _factory.ApiClient(_a.ApiKey);
        var first = await c.PostAsJsonAsync("/api/v1/endpoints", new RegisterEndpointRequest("win-1", "windows", "Desk"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await c.PostAsJsonAsync("/api/v1/endpoints", new RegisterEndpointRequest("win-1", "windows", "Desk"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Revoked_key_is_rejected()
    {
        using var scope = _factory.Services.CreateScope();
        var creds = scope.ServiceProvider.GetRequiredService<Services.CredentialService>();
        var (cred, secret) = await creds.IssueApiKeyAsync(_a.TenantId, _a.AccountId, "temp", [Scopes.Publish], null, CancellationToken.None);
        var c = _factory.ApiClient(secret);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me")).StatusCode);
        await creds.RevokeAsync(_a.TenantId, cred.Id, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
    }
}
