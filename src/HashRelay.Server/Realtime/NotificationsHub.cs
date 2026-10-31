using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace HashRelay.Server.Realtime;

/// <summary>
/// Connected-delivery transport for subscriber agents. Every connection is authenticated with a tenant-bound credential
/// holding the <c>subscribe</c> scope; the endpoint identity is bound server-side in Hello, and there is no client-initiated
/// broadcast. Groups are per-endpoint routing addresses, never the subscription store.
/// </summary>
[Authorize(AuthenticationSchemes = ApiAuth.Scheme, Policy = ApiAuth.PolicySubscribe)]
public sealed class NotificationsHub(EndpointService endpoints, RoutingService routing, ConnectionRegistry registry, ILogger<NotificationsHub> log) : Hub
{
    private const string ItemEndpoint = "hr:endpoint";

    public async Task<HelloResponse> Hello(HelloRequest req)
    {
        var user = Context.User!;
        var tenantId = user.TenantId();
        try
        {
            var ep = await endpoints.RegisterAsync(tenantId, req.EndpointId, req.Name, req.Platform, req.AgentVersion, req.PushProvider, req.PushToken, user.BoundEndpointRef(), connected: true, Context.ConnectionAborted);
            registry.Attach(Context.ConnectionId, ep.Id, user.CredentialId());
            await Groups.AddToGroupAsync(Context.ConnectionId, EndpointService.GroupFor(ep.Id), Context.ConnectionAborted);
            Context.Items[ItemEndpoint] = ep.Id;
            var subs = EndpointService.CurrentTags(ep);
            var replayed = await routing.ReplayAsync(tenantId, ep.Id, Context.ConnectionId, Context.ConnectionAborted);
            log.LogInformation("Endpoint {EndpointId} ({Name}) connected in tenant {TenantId}; {Subs} subscriptions, {Replayed} replayed",
                ep.EndpointId, ep.Name, tenantId, subs.Count, replayed);
            return new HelloResponse(new TenantInfo(tenantId, user.TenantSlug(), user.TenantName()), ep.EndpointId, ep.Name, subs, DateTimeOffset.UtcNow, replayed);
        }
        catch (ApiException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public Task<IReadOnlyList<string>> Subscribe(string[] tags) => Mutate(ep => endpoints.AddSubscriptionsAsync(ep, tags, Context.ConnectionAborted));
    public Task<IReadOnlyList<string>> Unsubscribe(string[] tags) => Mutate(ep => endpoints.RemoveSubscriptionsAsync(ep, tags, Context.ConnectionAborted));

    private async Task<IReadOnlyList<string>> Mutate(Func<Data.Endpoint, Task<IReadOnlyList<string>>> action)
    {
        var epRef = RequireRegistered();
        var tenantId = Context.User!.TenantId();
        try
        {
            var ep = await endpoints.GetByRefAsync(tenantId, epRef, Context.ConnectionAborted)
                     ?? throw new HubException("Endpoint is no longer registered.");
            return await action(ep);
        }
        catch (ApiException ex) { throw new HubException(ex.Message); }
    }

    public async Task Ack(Guid deliveryId)
    {
        var epRef = RequireRegistered();
        await routing.AckAsync(Context.User!.TenantId(), epRef, deliveryId, Context.ConnectionAborted);
    }

    private Guid RequireRegistered()
        => Context.Items.TryGetValue(ItemEndpoint, out var v) && v is Guid g ? g : throw new HubException("Call Hello before other hub methods.");

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var (epRef, nowOffline) = registry.Detach(Context.ConnectionId);
        if (epRef is not null && nowOffline)
        {
            try { await endpoints.MarkDisconnectedAsync(epRef.Value, CancellationToken.None); }
            catch (Exception ex) { log.LogDebug(ex, "Failed to record disconnect for {EndpointRef}", epRef); }
        }
        await base.OnDisconnectedAsync(exception);
    }
}
