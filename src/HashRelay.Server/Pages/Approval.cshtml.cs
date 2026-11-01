using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using HashRelay.Server.Services;
using HashRelay.Server.Services.Approvals;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HashRelay.Server.Pages;

/// <summary>
/// The decision page a notification links to. Visible only to a signed-in, current member of the approval's workspace
/// who is a designated approver; everyone else gets 404. The posted form is antiforgery-checked by Razor Pages and
/// carries a per-render idempotency key, so a double submit records one decision.
/// </summary>
[Authorize]
public sealed class ApprovalModel(ApprovalService approvals, TimeProvider clock) : PageModel
{
    public Approval Item { get; private set; } = null!;
    public string? Error { get; private set; }
    public string? Done { get; private set; }
    public string DecisionKey { get; private set; } = "";
    public bool CanDecide => Item.Status == ApprovalStatuses.Pending && clock.GetUtcNow() < Item.ExpiresAt;

    /// <summary>The choice pre-selected by a toast button (<c>?decision=approve|reject</c>). It only changes emphasis: nothing is recorded until the form is posted.</summary>
    [BindProperty(SupportsGet = true, Name = "decision")] public string? Intended { get; set; }
    public bool IntendsReject => string.Equals(Intended, ApprovalDecisions.Reject, StringComparison.OrdinalIgnoreCase);
    public bool IntendsApprove => string.Equals(Intended, ApprovalDecisions.Approve, StringComparison.OrdinalIgnoreCase);

    [BindProperty] public string? Note { get; set; }
    [BindProperty] public string? Key { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => await LoadAsync(id, ct) ? Page() : NotFound();

    public Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken ct) => DecideAsync(id, ApprovalDecisions.Approve, ct);
    public Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken ct) => DecideAsync(id, ApprovalDecisions.Reject, ct);

    private async Task<IActionResult> DecideAsync(Guid id, string decision, CancellationToken ct)
    {
        var accountId = CurrentTenant.AccountIdOf(User);
        if (accountId is null) return Challenge();
        try
        {
            var r = await approvals.DecideAsync(accountId.Value, id, new ApprovalDecisionRequest(decision, Note), Key, HttpContext.TraceIdentifier, ct);
            Done = r.Status == ApprovalStatuses.Approved ? "You approved this request." : "You rejected this request.";
        }
        catch (ApiException ex) when (ex.Status == 404) { return NotFound(); }
        catch (ApiException ex) { Error = ex.Message; }
        return await LoadAsync(id, ct) ? Page() : NotFound();
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken ct)
    {
        var accountId = CurrentTenant.AccountIdOf(User);
        var item = accountId is null ? null : await approvals.FindForApproverAsync(accountId.Value, id, ct);
        if (item is null) return false;
        Item = item;
        DecisionKey = Guid.NewGuid().ToString("N");
        return true;
    }
}

/// <summary>Pending approvals waiting on the signed-in person.</summary>
[Authorize]
public sealed class ApprovalsModel(ApprovalService approvals) : PageModel
{
    public List<Approval> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var accountId = CurrentTenant.AccountIdOf(User);
        if (accountId is not null) Items = await approvals.PendingForApproverAsync(accountId.Value, ct);
    }
}
