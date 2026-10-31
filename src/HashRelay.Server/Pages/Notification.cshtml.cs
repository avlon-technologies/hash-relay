using HashRelay.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HashRelay.Server.Pages;

/// <summary>Full view of one notification: rendered markdown, live Mermaid diagram, image and link. Members of its workspace only.</summary>
[Authorize]
public sealed class NotificationModel(CurrentTenant current, RoutingService routing) : PageModel
{
    public Notification Item { get; private set; } = null!;
    public string TenantName { get; private set; } = "";
    public string? MarkdownHtml { get; private set; }
    public int Delivered { get; private set; }
    /// <summary>Accepted by the push provider but not yet acknowledged by the device: not evidence of display.</summary>
    public int Pushed { get; private set; }
    public int Acked { get; private set; }
    public bool ShowImage => Item.ImageUrl is not null;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        await current.LoadAsync(ct);
        var item = await routing.FindAsync(id, current.Memberships.Select(m => m.TenantId), ct);
        if (item is null) return NotFound();
        Item = item;
        TenantName = current.Memberships.First(m => m.TenantId == item.TenantId).Tenant.Name;
        Delivered = item.Deliveries.Count(d => d.Status != DeliveryStatus.Queued);
        Pushed = item.Deliveries.Count(d => d.Status == DeliveryStatus.Pushed);
        Acked = item.Deliveries.Count(d => d.Status == DeliveryStatus.Acked);
        if (item.Markdown is not null) MarkdownHtml = MarkdownRenderer.ToHtml(item.Markdown, item.Title);
        return Page();
    }
}
