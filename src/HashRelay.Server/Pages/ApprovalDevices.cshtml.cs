using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using HashRelay.Server.Services;
using HashRelay.Server.Services.Approvals;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HashRelay.Server.Pages;

/// <summary>
/// Grant (GrantDeviceDecisions) or revoke (RevokeDeviceDecisions) one-click approvals for a paired device. Signed-in
/// only; the grant is the signed-in person's and only the person who paired the device may give it.
/// </summary>
public sealed class ApprovalDevicesModel(ApprovalService approvals, CurrentTenant current) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }
    public ApprovalService.GrantRequestView? Request { get; private set; }
    public bool CodeNotFound { get; private set; }
    public bool NotYours { get; private set; }
    public string? Error { get; private set; }
    public bool Granted { get; private set; }
    public bool Denied { get; private set; }
    public string? Revoked { get; private set; }
    public string DeviceName { get; private set; } = "";
    public List<DecisionGrant> Grants { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await current.LoadAsync(ct);
        if (current.Account is null) return Challenge();
        if (!string.IsNullOrWhiteSpace(Code))
        {
            Request = await approvals.FindDecisionGrantRequestAsync(Code, ct);
            CodeNotFound = Request is null;
            NotYours = Request is not null && Request.PairedByAccountId != current.Account.Id;
        }
        Grants = await approvals.DecisionGrantsForAsync(current.Account.Id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostGrantAsync(Guid grantId, bool understood, CancellationToken ct)
    {
        await current.LoadAsync(ct);
        if (current.Account is null) return Challenge();
        if (!understood)
        {
            Error = "Tick the box to confirm you understand what one-click approval allows.";
            return await OnGetAsync(ct);
        }
        try
        {
            await approvals.GrantDeviceDecisionsAsync(current.Account.Id, grantId, ct);
            Granted = true;
            Grants = await approvals.DecisionGrantsForAsync(current.Account.Id, ct);
            DeviceName = Grants.FirstOrDefault(g => g.Id == grantId)?.DeviceName ?? "the device";
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
            return await OnGetAsync(ct);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostDenyAsync(Guid grantId, CancellationToken ct)
    {
        await current.LoadAsync(ct);
        if (current.Account is null) return Challenge();
        await approvals.DenyDecisionGrantAsync(current.Account.Id, grantId, ct);
        Denied = true;
        Grants = await approvals.DecisionGrantsForAsync(current.Account.Id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid grantId, CancellationToken ct)
    {
        await current.LoadAsync(ct);
        if (current.Account is null) return Challenge();
        var g = (await approvals.DecisionGrantsForAsync(current.Account.Id, ct)).FirstOrDefault(x => x.Id == grantId);
        if (g is not null && await approvals.RevokeDecisionGrantAsync(current.Account.Id, grantId, ct)) Revoked = g.DeviceName;
        Code = null;
        Grants = await approvals.DecisionGrantsForAsync(current.Account.Id, ct);
        return Page();
    }
}
