using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages.Dashboard;

public sealed class ServiceAccountModel(CurrentTenant current, IOptions<HashRelayOptions> options, ServiceAccountService accounts, AuditService audit, OAuthService oauth, TimeProvider clock)
    : DashboardPageModel(current, options)
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    public ServiceAccount Account { get; private set; } = null!;
    public List<Credential> Keys { get; private set; } = [];
    public List<AuditEvent> Activity { get; private set; } = [];
    public string? NewSecret { get; private set; }
    public string? NewLabel { get; private set; }
    /// <summary>Set when this service account was created by an OAuth consent (a connected app such as claude.ai).</summary>
    public OAuthClient? ConnectedClient { get; private set; }
    public OAuthGrant? Grant { get; private set; }
    public string McpUrl => McpUrlHelper.Resolve(options.Value, PublicUrl);
    public DateTimeOffset Now => clock.GetUtcNow();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var account = await accounts.GetAsync(Current.TenantId, Id, ct);
        if (account is null) return NotFound();
        Account = account;
        NewSecret = TempData["svckey.secret"] as string;
        NewLabel = TempData["svckey.label"] as string;
        Keys = await accounts.KeysAsync(Current.TenantId, Id, ct);
        Activity = await audit.ForServiceAccountAsync(Current.TenantId, Id, 50, ct);
        Grant = await oauth.GrantForServiceAccountAsync(Current.TenantId, Id, ct);
        if (Grant is not null) ConnectedClient = await oauth.ClientOfGrantAsync(Grant, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(string name, string? description, string[] scopes, string allowedTags, string? allowedUrlHosts, int hourlyLimit, string? publisherType, CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only workspace owners and admins can change service accounts.");
        try
        {
            var draft = ServiceAccountService.Validate(name, description, scopes, allowedTags, allowedUrlHosts, hourlyLimit, publisherType);
            await accounts.UpdateAsync(Current.TenantId, Id, Current.Account!.Id, draft, ct);
            return Flash("success", "Service account updated. Keys pick up the new policy on their next request.");
        }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }

    public async Task<IActionResult> OnPostEnableAsync(bool enabled, CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only workspace owners and admins can change service accounts.");
        try
        {
            await accounts.SetEnabledAsync(Current.TenantId, Id, Current.Account!.Id, enabled, ct);
            return Flash("success", enabled ? "Service account enabled." : "Service account disabled. Every key stops working immediately.");
        }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }

    public async Task<IActionResult> OnPostIssueKeyAsync(string? label, int expiresDays, CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only workspace owners and admins can issue keys.");
        try
        {
            var expires = expiresDays > 0 ? clock.GetUtcNow().AddDays(expiresDays) : (DateTimeOffset?)null;
            var (cred, secret) = await accounts.IssueKeyAsync(Current.TenantId, Id, Current.Account!.Id, label, expires, ct);
            TempData["svckey.secret"] = secret;
            TempData["svckey.label"] = cred.Label;
            return Redirect($"/dashboard/service-accounts/{Id}");
        }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid credentialId, CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only workspace owners and admins can revoke keys.");
        try
        {
            await accounts.RevokeKeyAsync(Current.TenantId, Id, credentialId, Current.Account!.Id, ct);
            return Flash("success", "Key revoked.");
        }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }

    public async Task<IActionResult> OnPostDisconnectAsync(CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only workspace owners and admins can disconnect apps.");
        try
        {
            await oauth.DisconnectAsync(Current.TenantId, Id, Current.Account!.Id, ct);
            return Flash("success", "App disconnected. Its tokens were revoked and the service account is disabled.");
        }
        catch (ApiException ex) { return Flash("danger", ex.Message); }
    }

    public static string ActionLabel(string action) => action switch
    {
        AuditActions.ServiceAccountCreated => "Created",
        AuditActions.ServiceAccountUpdated => "Policy changed",
        AuditActions.ServiceAccountEnabled => "Enabled",
        AuditActions.ServiceAccountDisabled => "Disabled",
        AuditActions.KeyIssued => "Key issued",
        AuditActions.KeyRevoked => "Key revoked",
        AuditActions.Published => "Published",
        AuditActions.PublishRefused => "Refused",
        AuditActions.SubscriptionsChanged => "Subscriptions changed",
        AuditActions.OAuthGranted => "App connected",
        AuditActions.OAuthRefreshed => "Token refreshed",
        AuditActions.OAuthRevoked => "App disconnected",
        _ => action,
    };

    public static string ActionBadge(string action) => action switch
    {
        AuditActions.PublishRefused or AuditActions.ServiceAccountDisabled or AuditActions.KeyRevoked or AuditActions.OAuthRevoked => "badge-danger",
        AuditActions.Published => "badge-success",
        _ => "badge-accent",
    };
}
