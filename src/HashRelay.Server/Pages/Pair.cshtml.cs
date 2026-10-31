using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HashRelay.Server.Pages;

public sealed class PairModel(DevicePairingService pairing, CurrentTenant current) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }
    public DevicePairing? Pairing { get; private set; }
    public bool CodeNotFound { get; private set; }
    public bool Approved { get; private set; }
    public bool Denied { get; private set; }
    public string DeviceName { get; private set; } = "";
    public string WorkspaceName { get; private set; } = "";
    public IReadOnlyList<Membership> Memberships => current.Memberships;
    public Guid? CurrentTenantId => current.HasTenant ? current.TenantId : null;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await current.LoadAsync(ct);
        if (!current.HasTenant) return Redirect("/dashboard/settings?nows=1");
        if (string.IsNullOrWhiteSpace(Code)) return Page();
        Pairing = await pairing.FindPendingAsync(Code, ct);
        CodeNotFound = Pairing is null;
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid pairingId, Guid tenantId, string[] scopes, CancellationToken ct)
    {
        await current.LoadAsync(ct);
        var membership = current.Memberships.FirstOrDefault(m => m.TenantId == tenantId);
        if (membership is null) return Forbid();
        try
        {
            var cred = await pairing.ApproveAsync(pairingId, current.Account!.Id, tenantId, scopes, ct);
            Approved = true;
            DeviceName = cred.Label;
            WorkspaceName = membership.Tenant.Name;
            return Page();
        }
        catch (ApiException ex)
        {
            CodeNotFound = true;
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDenyAsync(Guid pairingId, CancellationToken ct)
    {
        await current.LoadAsync(ct);
        await pairing.DenyAsync(pairingId, ct);
        Denied = true;
        return Page();
    }
}
