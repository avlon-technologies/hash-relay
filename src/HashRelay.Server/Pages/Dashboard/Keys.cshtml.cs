using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages.Dashboard;

public sealed class KeysModel(CurrentTenant current, IOptions<HashRelayOptions> options, CredentialService credentials, TimeProvider clock)
    : DashboardPageModel(current, options)
{
    public List<Credential> Keys { get; private set; } = [];
    public string? NewSecret { get; private set; }
    public string? NewLabel { get; private set; }
    public bool OpenCreate { get; private set; }

    public async Task OnGetAsync(bool create, CancellationToken ct)
    {
        OpenCreate = create && CanManage;
        NewSecret = TempData["newkey.secret"] as string;
        NewLabel = TempData["newkey.label"] as string;
        Keys = await credentials.ListAsync(Current.TenantId, CredentialKinds.Api, ct);
    }

    public async Task<IActionResult> OnPostCreateAsync(string label, string[] scopes, int expiresDays, string? publisherType, CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only workspace owners and admins can create keys.");
        try
        {
            var expires = expiresDays > 0 ? clock.GetUtcNow().AddDays(expiresDays) : (DateTimeOffset?)null;
            var (cred, secret) = await credentials.IssueApiKeyAsync(Current.TenantId, Current.Account!.Id, label, scopes, expires, ct, publisherType);
            TempData["newkey.secret"] = secret;
            TempData["newkey.label"] = cred.Label;
            return Redirect("/dashboard/keys");
        }
        catch (ApiException ex)
        {
            return Flash("danger", ex.Message);
        }
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only workspace owners and admins can revoke keys.");
        try
        {
            await credentials.RevokeAsync(Current.TenantId, id, ct);
            return Flash("success", "Key revoked.");
        }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }
}
