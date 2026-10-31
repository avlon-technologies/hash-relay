using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace HashRelay.Server.Pages.OAuth;

/// <summary>OAuth 2.1 authorization endpoint: the consent screen. Needs the dashboard session, so unauthenticated visitors go through /login first.</summary>
[Authorize]
public sealed class AuthorizeModel(CurrentTenant current, OAuthService oauth) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "client_id")] public string? ClientId { get; set; }
    [BindProperty(SupportsGet = true, Name = "redirect_uri")] public string? RedirectUri { get; set; }
    [BindProperty(SupportsGet = true, Name = "response_type")] public string? ResponseType { get; set; }
    [BindProperty(SupportsGet = true, Name = "scope")] public string? Scope { get; set; }
    [BindProperty(SupportsGet = true, Name = "state")] public string? State { get; set; }
    [BindProperty(SupportsGet = true, Name = "code_challenge")] public string? CodeChallenge { get; set; }
    [BindProperty(SupportsGet = true, Name = "code_challenge_method")] public string? CodeChallengeMethod { get; set; }
    [BindProperty(SupportsGet = true, Name = "resource")] public string? Resource { get; set; }

    public OAuthClient? Client { get; private set; }
    public string? FatalError { get; private set; }
    public IReadOnlyList<string> RequestedScopes { get; private set; } = [];
    public IReadOnlyList<Membership> Memberships => current.Memberships.Where(m => Roles.CanManageKeys(m.Role)).ToList();
    public Guid? CurrentTenantId => current.HasTenant ? current.TenantId : null;
    public string AccountEmail => current.Account?.Email ?? "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await current.LoadAsync(ct);
        if (await ValidateAsync(ct) is { } early) return early;
        RequestedScopes = OAuthService.ParseScopes(Scope);
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid tenantId, string[] scopes, string allowedTags, int hourlyLimit, CancellationToken ct)
    {
        await current.LoadAsync(ct);
        if (await ValidateAsync(ct) is { } early) return early;
        var membership = current.Memberships.FirstOrDefault(m => m.TenantId == tenantId);
        if (membership is null || !Roles.CanManageKeys(membership.Role)) return Fail("access_denied", "You are not an owner or admin of that workspace.");
        try
        {
            var granted = OAuthService.ParseScopes(string.Join(' ', scopes));
            var code = await oauth.ApproveAsync(tenantId, current.Account!, Client!, RedirectUri!, granted, allowedTags, hourlyLimit, CodeChallenge!, Resource, ct);
            return Redirect(QueryHelpers.AddQueryString(RedirectUri!, Query(("code", code), ("state", State))));
        }
        catch (ApiException ex)
        {
            RequestedScopes = OAuthService.ParseScopes(Scope);
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDenyAsync(CancellationToken ct)
    {
        await current.LoadAsync(ct);
        if (await ValidateAsync(ct) is { } early) return early;
        return Fail("access_denied", "The user declined the request.");
    }

    /// <summary>Client and redirect URI must be valid before anything is sent to the redirect URI; other problems are reported to it.</summary>
    private async Task<IActionResult?> ValidateAsync(CancellationToken ct)
    {
        Client = await oauth.FindClientAsync(ClientId, ct);
        if (Client is null) { FatalError = "Unknown client. The app that sent you here is not registered with this HashRelay server."; return Page(); }
        if (!OAuthService.RedirectAllowed(Client, RedirectUri)) { FatalError = "The redirect URI does not match the registered client."; return Page(); }
        if (ResponseType != "code") return Fail("unsupported_response_type", "Only response_type=code is supported.");
        if (string.IsNullOrWhiteSpace(CodeChallenge) || CodeChallengeMethod != "S256") return Fail("invalid_request", "PKCE with code_challenge_method=S256 is required.");
        if (!current.HasTenant) { FatalError = "Your account has no workspace yet. Create one in the dashboard, then try again."; return Page(); }
        return null;
    }

    private IActionResult Fail(string error, string description)
        => Redirect(QueryHelpers.AddQueryString(RedirectUri!, Query(("error", error), ("error_description", description), ("state", State))));

    private static Dictionary<string, string?> Query(params (string Key, string? Value)[] items)
        => items.Where(i => i.Value is not null).ToDictionary(i => i.Key, i => i.Value);

    public static string Describe(string scope) => ServiceScopes.Descriptions.TryGetValue(scope, out var d) ? d : scope;
}
