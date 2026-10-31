using System.Security.Claims;
using System.Text.Encodings.Web;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Auth;

public static class ApiAuth
{
    public const string Scheme = "apikey";
    public const string ClaimTenantId = "hr:tenant";
    public const string ClaimTenantSlug = "hr:tenant_slug";
    public const string ClaimTenantName = "hr:tenant_name";
    public const string ClaimCredentialId = "hr:cred";
    public const string ClaimCredentialKind = "hr:cred_kind";
    public const string ClaimEndpointRef = "hr:endpoint";
    public const string ClaimScope = "scope";

    public const string PolicyAny = "api:any";
    public const string PolicyPublish = "api:publish";
    public const string PolicySubscribe = "api:subscribe";

    public static Guid TenantId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTenantId)!);
    public static Guid CredentialId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimCredentialId)!);
    public static string CredentialKind(this ClaimsPrincipal p) => p.FindFirstValue(ClaimCredentialKind) ?? "";
    public static Guid? BoundEndpointRef(this ClaimsPrincipal p)
        => Guid.TryParse(p.FindFirstValue(ClaimEndpointRef), out var g) ? g : null;
    public static string TenantName(this ClaimsPrincipal p) => p.FindFirstValue(ClaimTenantName) ?? "";
    public static string TenantSlug(this ClaimsPrincipal p) => p.FindFirstValue(ClaimTenantSlug) ?? "";
}

/// <summary>
/// Authenticates API and hub requests with a tenant-bound credential presented as
/// <c>Authorization: Bearer hr_…</c> or, for WebSocket negotiation, <c>?access_token=</c>.
/// The tenant is resolved from the credential, never from client-supplied input.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    RelayDbContext db,
    TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractToken();
        if (token is null) return AuthenticateResult.NoResult();
        if (!Secrets.LooksLikeSecret(token)) return AuthenticateResult.Fail("Malformed credential.");

        var hash = Secrets.Sha256Hex(token);
        var now = clock.GetUtcNow();
        var cred = await db.Credentials.AsNoTracking()
            .Where(c => c.Hash == hash)
            .Select(c => new { c.Id, c.TenantId, c.Kind, c.Scopes, c.EndpointRef, c.RevokedAt, c.ExpiresAt, c.LastUsedAt, Tenant = db.Tenants.First(t => t.Id == c.TenantId) })
            .FirstOrDefaultAsync(Context.RequestAborted);

        if (cred is null) return AuthenticateResult.Fail("Unknown credential.");
        if (cred.RevokedAt is not null) return AuthenticateResult.Fail("Credential revoked.");
        if (cred.ExpiresAt is not null && cred.ExpiresAt <= now) return AuthenticateResult.Fail("Credential expired.");

        // Touch LastUsedAt at most once a minute to avoid write amplification.
        if (cred.LastUsedAt is null || now - cred.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            try
            {
                await db.Credentials.Where(c => c.Id == cred.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, now), Context.RequestAborted);
            }
            catch (DbUpdateException) { /* non-critical */ }
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, cred.Id.ToString()),
            new(ClaimTypes.Name, $"{cred.Kind}:{cred.Id:n}"),
            new(ApiAuth.ClaimCredentialId, cred.Id.ToString()),
            new(ApiAuth.ClaimCredentialKind, cred.Kind),
            new(ApiAuth.ClaimTenantId, cred.TenantId.ToString()),
            new(ApiAuth.ClaimTenantSlug, cred.Tenant.Slug),
            new(ApiAuth.ClaimTenantName, cred.Tenant.Name),
        };
        if (cred.EndpointRef is not null) claims.Add(new Claim(ApiAuth.ClaimEndpointRef, cred.EndpointRef.Value.ToString()));
        foreach (var s in cred.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            claims.Add(new Claim(ApiAuth.ClaimScope, s));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private string? ExtractToken()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
        {
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth[7..].Trim();
            return null;
        }
        if (Request.Query.TryGetValue("access_token", out var q) && !string.IsNullOrEmpty(q)) return q.ToString();
        if (Request.Headers.TryGetValue("X-HashRelay-Key", out var h) && !string.IsNullOrEmpty(h)) return h.ToString();
        return null;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"HashRelay\"";
        return ApiErrors.WriteAsync(Context, StatusCodes.Status401Unauthorized, Contracts.ErrorCodes.Unauthenticated,
            "A valid API key or device token is required. Send it as 'Authorization: Bearer hr_…'.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => ApiErrors.WriteAsync(Context, StatusCodes.Status403Forbidden, Contracts.ErrorCodes.Forbidden,
            "This credential does not have permission for that operation.");
}
