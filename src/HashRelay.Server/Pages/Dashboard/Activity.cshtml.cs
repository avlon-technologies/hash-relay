using HashRelay.Contracts;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages.Dashboard;

public sealed class ActivityModel(CurrentTenant current, IOptions<HashRelayOptions> options, RoutingService routing) : DashboardPageModel(current, options)
{
    public List<NotificationSummary> Recent { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
        => Recent = await routing.RecentAsync(Current.TenantId, 100, ct);
}
