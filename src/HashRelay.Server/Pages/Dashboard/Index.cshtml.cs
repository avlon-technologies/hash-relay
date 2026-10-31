using HashRelay.Contracts;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages.Dashboard;

public sealed class IndexModel(CurrentTenant current, IOptions<HashRelayOptions> options, RelayDbContext db, ConnectionRegistry registry, RoutingService routing, TimeProvider clock)
    : DashboardPageModel(current, options)
{
    public bool Welcome { get; private set; }
    public int Online { get; private set; }
    public int Devices { get; private set; }
    public int Last24h { get; private set; }
    public int Total { get; private set; }
    public int ActiveKeys { get; private set; }
    public int Subscriptions { get; private set; }
    public int DistinctTags { get; private set; }
    public List<NotificationSummary> Recent { get; private set; } = [];

    public async Task OnGetAsync(bool welcome, CancellationToken ct)
    {
        Welcome = welcome;
        var tid = Current.TenantId;
        var now = clock.GetUtcNow();
        var endpointIds = await db.Endpoints.Where(e => e.TenantId == tid).Select(e => e.Id).ToListAsync(ct);
        Devices = endpointIds.Count;
        Online = endpointIds.Count(registry.IsOnline);
        Last24h = await db.Notifications.CountAsync(n => n.TenantId == tid && n.PublishedAt > now.AddHours(-24), ct);
        Total = await db.Notifications.CountAsync(n => n.TenantId == tid, ct);
        ActiveKeys = await db.Credentials.CountAsync(c => c.TenantId == tid && c.Kind == CredentialKinds.Api && c.RevokedAt == null, ct);
        Subscriptions = await db.Subscriptions.CountAsync(s => s.TenantId == tid, ct);
        DistinctTags = await db.Subscriptions.Where(s => s.TenantId == tid).Select(s => s.Tag).Distinct().CountAsync(ct);
        Recent = await routing.RecentAsync(tid, 8, ct);
    }

    public IActionResult OnPostSwitch(Guid tenantId)
    {
        if (Current.Memberships.Any(m => m.TenantId == tenantId)) Current.Switch(tenantId);
        return Redirect(Request.Headers.Referer.ToString() is { Length: > 0 } r && Url.IsLocalUrl(new Uri(r).PathAndQuery) ? new Uri(r).PathAndQuery : "/dashboard");
    }
}
