using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages.Dashboard;

public sealed class DevicesModel(CurrentTenant current, IOptions<HashRelayOptions> options, EndpointService endpoints, CredentialService credentials, ConnectionRegistry registry)
    : DashboardPageModel(current, options)
{
    public sealed record DeviceRow(Data.Endpoint Endpoint, bool Online, IReadOnlyList<string> Tags, Credential? Token);

    public List<DeviceRow> Devices { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var tid = Current.TenantId;
        var eps = await endpoints.ListAsync(tid, ct);
        var tokens = (await credentials.ListAsync(tid, CredentialKinds.Device, ct)).Where(c => c.RevokedAt is null).ToList();
        Devices = eps.Select(e => new DeviceRow(e, registry.IsOnline(e.Id), EndpointService.CurrentTags(e), tokens.FirstOrDefault(t => t.EndpointRef == e.Id))).ToList();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only owners and admins can remove devices.");
        try { await endpoints.DeleteAsync(Current.TenantId, id, ct); return Flash("success", "Device removed."); }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }

    public async Task<IActionResult> OnPostAddTagAsync(Guid id, string tag, CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only owners and admins can change subscriptions.");
        try
        {
            var ep = await endpoints.GetByRefAsync(Current.TenantId, id, ct) ?? throw ApiException.NotFound("Device not found.");
            await endpoints.AddSubscriptionsAsync(ep, [tag], ct);
            return Redirect("/dashboard/devices");
        }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }

    public async Task<IActionResult> OnPostRemoveTagAsync(Guid id, string tag, CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only owners and admins can change subscriptions.");
        try
        {
            var ep = await endpoints.GetByRefAsync(Current.TenantId, id, ct) ?? throw ApiException.NotFound("Device not found.");
            await endpoints.RemoveSubscriptionsAsync(ep, [tag], ct);
            return Redirect("/dashboard/devices");
        }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }
}
