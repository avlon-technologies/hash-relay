using HashRelay.Server.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages.Dashboard;

public sealed class SettingsModel(CurrentTenant current, IOptions<HashRelayOptions> options, AccountService accounts) : DashboardPageModel(current, options)
{
    public bool NoWorkspace { get; private set; }
    public bool HasTenant => Current.HasTenant;
    public string Slug => Current.Tenant?.Slug ?? "";
    public Guid? TenantId => Current.HasTenant ? Current.TenantId : null;
    public string Role => Current.Role;
    public string AccountName => Current.Account?.DisplayName ?? "";
    public string AccountEmail => Current.Account?.Email ?? "";

    public void OnGet(bool nows) => NoWorkspace = nows && !Current.HasTenant;

    public async Task<IActionResult> OnPostRenameAsync(string name, CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only owners and admins can rename the workspace.");
        try { await accounts.RenameTenantAsync(Current.TenantId, name, ct); return Flash("success", "Workspace renamed."); }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }

    public async Task<IActionResult> OnPostPasswordAsync(string currentPassword, string newPassword, string confirmPassword, CancellationToken ct)
    {
        if (newPassword != confirmPassword) return Flash("danger", "The new passwords do not match.");
        try { await accounts.ChangePasswordAsync(Current.Account!.Id, currentPassword ?? "", newPassword ?? "", ct); return Flash("success", "Password changed."); }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }

    public async Task<IActionResult> OnPostCreateAsync(string name, CancellationToken ct)
    {
        try
        {
            var tenant = await accounts.CreateTenantAsync(Current.Account!.Id, name, ct);
            Current.Switch(tenant.Id);
            return Flash("success", $"Workspace “{tenant.Name}” created.", "/dashboard");
        }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }
}
