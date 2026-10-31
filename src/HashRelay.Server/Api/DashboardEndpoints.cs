using HashRelay.Contracts;
using HashRelay.Server.Data;
using HashRelay.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Api;

/// <summary>Cookie-authenticated JSON used by the dashboard for live refresh.</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard/live", async (CurrentTenant current, RelayDbContext db, ConnectionRegistry registry, RoutingService routing, EndpointService endpoints, TimeProvider clock, CancellationToken ct) =>
        {
            await current.LoadAsync(ct);
            if (!current.HasTenant) return Results.Unauthorized();
            var tid = current.TenantId;
            var now = clock.GetUtcNow();
            var eps = await endpoints.ListAsync(tid, ct);
            var stats = new Dictionary<string, int>
            {
                ["online"] = eps.Count(e => registry.IsOnline(e.Id)),
                ["last24h"] = await db.Notifications.CountAsync(n => n.TenantId == tid && n.PublishedAt > now.AddHours(-24), ct),
                ["keys"] = await db.Credentials.CountAsync(c => c.TenantId == tid && c.Kind == CredentialKinds.Api && c.RevokedAt == null, ct),
                ["subscriptions"] = await db.Subscriptions.CountAsync(s => s.TenantId == tid, ct),
            };
            var recent = await routing.RecentAsync(tid, 100, ct);
            return Results.Ok(new { stats, endpoints = eps.Select(endpoints.ToInfo), recent, serverTime = now });
        }).RequireAuthorization();
        return app;
    }
}
