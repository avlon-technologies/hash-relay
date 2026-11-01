using System.Security.Cryptography;
using System.Text;
using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Services;

/// <summary>An OAuth 2.1 protocol error, rendered as <c>{error, error_description}</c>.</summary>
public sealed class OAuthException(string error, string description, int status = 400) : Exception(description)
{
    public string Error { get; } = error;
    public int Status { get; } = status;
}

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken, string Scope);

/// <summary>
/// HashRelay as an OAuth 2.1 authorization server for the MCP endpoint: dynamic client registration, PKCE-only
/// authorization codes, one-hour access tokens and rotating refresh tokens. A consent creates a service account for
/// (client, person, workspace), so OAuth tokens are ordinary service keys and inherit its policy and audit trail.
/// </summary>
public sealed class OAuthService(RelayDbContext db, TimeProvider clock, ServiceAccountService accounts, AuditService audit, ILogger<OAuthService> log)
{
    public const string ClientPrefix = "hrc_";
    public const string ClientSecretPrefix = "hrcs_";
    public const string CodePrefix = "hrac_";
    public const string RefreshPrefix = "hrr_";
    public const string AuthNone = "none";
    public const string AuthSecretPost = "client_secret_post";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);

    // ------------------------------------------------------------------ clients

    public sealed record ClientRegistration(string? ClientName, IReadOnlyList<string>? RedirectUris, string? TokenEndpointAuthMethod, string? ClientUri, string? LogoUri);

    public static string? RedirectUriProblem(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return $"'{uri}' is not an absolute URI.";
        if (u.Fragment.Length > 0) return "redirect URIs must not contain a fragment.";
        if (u.Scheme == Uri.UriSchemeHttp && !(u.IsLoopback)) return "http redirect URIs are only allowed for loopback addresses.";
        return null;
    }

    public async Task<(OAuthClient Client, string? Secret)> RegisterAsync(ClientRegistration reg, CancellationToken ct)
    {
        var name = (reg.ClientName ?? "").Trim();
        if (name.Length is < 1 or > 120) throw new OAuthException("invalid_client_metadata", "client_name is required (1–120 characters).");
        var uris = (reg.RedirectUris ?? []).Select(u => u.Trim()).Where(u => u.Length > 0).Distinct().ToList();
        if (uris.Count is 0 or > 10) throw new OAuthException("invalid_redirect_uri", "Provide 1–10 redirect_uris.");
        foreach (var u in uris) if (RedirectUriProblem(u) is { } problem) throw new OAuthException("invalid_redirect_uri", problem);
        var method = string.IsNullOrWhiteSpace(reg.TokenEndpointAuthMethod) ? AuthNone : reg.TokenEndpointAuthMethod.Trim();
        if (method is not (AuthNone or AuthSecretPost)) throw new OAuthException("invalid_client_metadata", $"token_endpoint_auth_method must be {AuthNone} or {AuthSecretPost}.");
        static string? Url(string? v) => string.IsNullOrWhiteSpace(v) ? null : Uri.TryCreate(v.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? u.ToString() : null;

        string? secret = method == AuthSecretPost ? ClientSecretPrefix + Secrets.RandomString(40) : null;
        var client = new OAuthClient
        {
            Id = ClientPrefix + Secrets.RandomString(24), Name = name, RedirectUris = string.Join('\n', uris), AuthMethod = method,
            SecretHash = secret is null ? null : Secrets.Sha256Hex(secret), ClientUri = Url(reg.ClientUri), LogoUri = Url(reg.LogoUri), CreatedAt = clock.GetUtcNow(),
        };
        db.OAuthClients.Add(client);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Registered OAuth client {ClientId} ({Name})", client.Id, client.Name);
        return (client, secret);
    }

    public Task<OAuthClient?> FindClientAsync(string? clientId, CancellationToken ct)
        => string.IsNullOrWhiteSpace(clientId) ? Task.FromResult<OAuthClient?>(null) : db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);

    public static bool RedirectAllowed(OAuthClient client, string? redirectUri)
        => redirectUri is not null && client.RedirectUriList.Contains(redirectUri, StringComparer.Ordinal);

    private static void AuthenticateClient(OAuthClient client, string? secret)
    {
        if (client.AuthMethod != AuthSecretPost) return;
        if (secret is null || client.SecretHash is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Secrets.Sha256Hex(secret)), Encoding.ASCII.GetBytes(client.SecretHash)))
            throw new OAuthException("invalid_client", "Client authentication failed.", 401);
    }

    // ------------------------------------------------------------------ consent → code

    public static IReadOnlyList<string> ParseScopes(string? scope)
    {
        var requested = (scope ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).Where(ServiceScopes.All.Contains).Distinct().ToList();
        return requested.Count == 0 ? [ServiceScopes.Publish] : ServiceScopes.All.Where(requested.Contains).ToList();
    }

    /// <summary>Records the person's consent (creating or updating the backing service account) and returns a fresh authorization code.</summary>
    /// <param name="publisherType">What the person says the connecting app is (a <see cref="PublisherTypes"/> value). Unknown when not stated; the OAuth/MCP transport never implies a type.</param>
    public async Task<string> ApproveAsync(Guid tenantId, Account account, OAuthClient client, string redirectUri, IReadOnlyList<string> scopes,
        string allowedTags, int hourlyLimit, string codeChallenge, string? resource, CancellationToken ct, string? publisherType = null)
    {
        var now = clock.GetUtcNow();
        var grant = await db.OAuthGrants.FirstOrDefaultAsync(g => g.TenantId == tenantId && g.AccountId == account.Id && g.ClientId == client.Id && g.RevokedAt == null, ct);
        var description = $"Connected app “{client.Name}”, authorised by {account.Email}";
        ServiceAccount sa;
        if (grant is not null && await accounts.GetAsync(tenantId, grant.ServiceAccountId, ct) is { } existing)
        {
            // Re-consent keeps a type the admin set on the account unless the person states one now.
            var type = string.IsNullOrWhiteSpace(publisherType) || PublisherTypes.Normalize(publisherType) == PublisherTypes.Unknown ? existing.PublisherType : publisherType;
            var draft = ServiceAccountService.Validate(existing.Name, description, scopes, allowedTags, existing.AllowedUrlHosts, hourlyLimit, type);
            sa = await accounts.UpdateAsync(tenantId, existing.Id, account.Id, draft, ct);
            if (!sa.Enabled) await accounts.SetEnabledAsync(tenantId, sa.Id, account.Id, true, ct);
            grant.Scopes = string.Join(',', scopes);
            grant.UpdatedAt = now;
        }
        else
        {
            sa = await CreateServiceAccountAsync(tenantId, account, client, description, scopes, allowedTags, hourlyLimit, publisherType, ct);
            grant = new OAuthGrant { Id = Guid.NewGuid(), TenantId = tenantId, AccountId = account.Id, ClientId = client.Id, ServiceAccountId = sa.Id, Scopes = string.Join(',', scopes), CreatedAt = now, UpdatedAt = now };
            db.OAuthGrants.Add(grant);
        }

        var code = CodePrefix + Secrets.RandomString(48);
        db.OAuthCodes.Add(new OAuthCode
        {
            Id = Guid.NewGuid(), CodeHash = Secrets.Sha256Hex(code), GrantId = grant.Id, ClientId = client.Id, RedirectUri = redirectUri,
            CodeChallenge = codeChallenge, Resource = resource, Scopes = grant.Scopes, ExpiresAt = now + CodeLifetime,
        });
        audit.Add(tenantId, AuditActions.OAuthGranted, sa.Id, accountId: account.Id, target: client.Name, detail: $"scopes {string.Join(' ', scopes)}; tags {allowedTags}");
        await db.SaveChangesAsync(ct);
        return code;
    }

    private async Task<ServiceAccount> CreateServiceAccountAsync(Guid tenantId, Account account, OAuthClient client, string description,
        IReadOnlyList<string> scopes, string allowedTags, int hourlyLimit, string? publisherType, CancellationToken ct)
    {
        var baseName = $"{client.Name} · {account.DisplayName}";
        if (baseName.Length > 70) baseName = baseName[..70].Trim();
        for (var attempt = 0; ; attempt++)
        {
            var name = attempt == 0 ? baseName : attempt < 4 ? $"{baseName} {attempt + 1}" : $"{baseName} {Secrets.RandomString(4).ToLowerInvariant()}";
            try
            {
                var draft = ServiceAccountService.Validate(name, description, scopes, allowedTags, null, hourlyLimit, publisherType);
                return await accounts.CreateAsync(tenantId, account.Id, draft, ct);
            }
            catch (ApiException ex) when (ex.Status == 409 && attempt < 6) { /* name taken, try the next */ }
        }
    }

    // ------------------------------------------------------------------ tokens

    public async Task<TokenResponse> ExchangeCodeAsync(string? code, string? clientId, string? clientSecret, string? redirectUri, string? codeVerifier, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(codeVerifier)) throw new OAuthException("invalid_request", "code and code_verifier are required.");
        var client = await FindClientAsync(clientId, ct) ?? throw new OAuthException("invalid_client", "Unknown client_id.", 401);
        AuthenticateClient(client, clientSecret);

        var now = clock.GetUtcNow();
        var hash = Secrets.Sha256Hex(code);
        var row = await db.OAuthCodes.FirstOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (row is null || row.ClientId != client.Id) throw new OAuthException("invalid_grant", "Unknown authorization code.");
        if (row.ConsumedAt is not null)
        {
            // Replay of a used code: revoke everything issued from its grant (RFC 6749 §4.1.2).
            await RevokeGrantTokensAsync(row.GrantId, now, ct);
            await db.SaveChangesAsync(ct);
            throw new OAuthException("invalid_grant", "Authorization code already used.");
        }
        if (row.ExpiresAt <= now) throw new OAuthException("invalid_grant", "Authorization code expired.");
        if (!string.Equals(row.RedirectUri, redirectUri, StringComparison.Ordinal)) throw new OAuthException("invalid_grant", "redirect_uri does not match.");
        if (!PkceMatches(row.CodeChallenge, codeVerifier)) throw new OAuthException("invalid_grant", "PKCE verification failed.");

        var grant = await db.OAuthGrants.FirstOrDefaultAsync(g => g.Id == row.GrantId && g.RevokedAt == null, ct) ?? throw new OAuthException("invalid_grant", "Consent was revoked.");
        var sa = await db.ServiceAccounts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == grant.ServiceAccountId, ct) ?? throw new OAuthException("invalid_grant", "Service account missing.");
        if (!sa.Enabled) throw new OAuthException("invalid_grant", "The service account for this connection is disabled.");

        row.ConsumedAt = now;
        var response = IssuePair(grant, client, sa, now);
        await db.OAuthClients.Where(c => c.Id == client.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, now), ct);
        await db.SaveChangesAsync(ct);
        log.LogInformation("OAuth code exchanged for client {ClientId}, service account {ServiceAccountId}", client.Id, sa.Id);
        return response;
    }

    public async Task<TokenResponse> RefreshAsync(string? refreshToken, string? clientId, string? clientSecret, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken)) throw new OAuthException("invalid_request", "refresh_token is required.");
        var client = await FindClientAsync(clientId, ct) ?? throw new OAuthException("invalid_client", "Unknown client_id.", 401);
        AuthenticateClient(client, clientSecret);

        var now = clock.GetUtcNow();
        var hash = Secrets.Sha256Hex(refreshToken);
        var token = await db.OAuthRefreshTokens.FirstOrDefaultAsync(t => t.Hash == hash, ct) ?? throw new OAuthException("invalid_grant", "Unknown refresh token.");
        var grant = await db.OAuthGrants.FirstOrDefaultAsync(g => g.Id == token.GrantId, ct) ?? throw new OAuthException("invalid_grant", "Consent not found.");
        if (grant.ClientId != client.Id) throw new OAuthException("invalid_grant", "Refresh token was issued to another client.");
        if (token.RevokedAt is not null)
        {
            // Reuse of a rotated token means it leaked: revoke the whole family.
            await RevokeGrantTokensAsync(grant.Id, now, ct);
            await db.SaveChangesAsync(ct);
            throw new OAuthException("invalid_grant", "Refresh token was already used; all tokens for this connection have been revoked.");
        }
        if (token.ExpiresAt <= now) throw new OAuthException("invalid_grant", "Refresh token expired.");
        if (grant.RevokedAt is not null) throw new OAuthException("invalid_grant", "Consent was revoked.");
        var sa = await db.ServiceAccounts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == grant.ServiceAccountId, ct) ?? throw new OAuthException("invalid_grant", "Service account missing.");
        if (!sa.Enabled) throw new OAuthException("invalid_grant", "The service account for this connection is disabled.");

        token.RevokedAt = now;
        await db.Credentials.Where(c => c.Id == token.AccessCredentialId && c.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, now), ct);
        var response = IssuePair(grant, client, sa, now, replaces: token);
        // Housekeeping: drop long-expired OAuth access credentials so the table does not grow forever.
        var cutoff = now.AddDays(-7);
        await db.Credentials.Where(c => c.ServiceAccountId == sa.Id && c.Label.StartsWith("OAuth ·") && c.ExpiresAt != null && c.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
        audit.Add(grant.TenantId, AuditActions.OAuthRefreshed, sa.Id, accountId: grant.AccountId, target: client.Name);
        await db.SaveChangesAsync(ct);
        return response;
    }

    private TokenResponse IssuePair(OAuthGrant grant, OAuthClient client, ServiceAccount sa, DateTimeOffset now, OAuthRefreshToken? replaces = null)
    {
        var access = Secrets.NewServiceKey();
        var cred = new Credential
        {
            Id = Guid.NewGuid(), TenantId = grant.TenantId, Kind = CredentialKinds.Service, Label = $"OAuth · {client.Name}",
            Hash = Secrets.Sha256Hex(access), Hint = Secrets.Hint(access), Scopes = sa.Scopes, ServiceAccountId = sa.Id,
            CreatedByAccountId = grant.AccountId, CreatedAt = now, ExpiresAt = now + AccessLifetime,
        };
        db.Credentials.Add(cred);
        var refresh = RefreshPrefix + Secrets.RandomString(48);
        var refreshRow = new OAuthRefreshToken { Id = Guid.NewGuid(), Hash = Secrets.Sha256Hex(refresh), GrantId = grant.Id, AccessCredentialId = cred.Id, CreatedAt = now, ExpiresAt = now + RefreshLifetime };
        db.OAuthRefreshTokens.Add(refreshRow);
        if (replaces is not null) replaces.ReplacedById = refreshRow.Id;
        return new TokenResponse(access, "Bearer", (int)AccessLifetime.TotalSeconds, refresh, string.Join(' ', sa.ScopeList));
    }

    public static bool PkceMatches(string challenge, string verifier)
    {
        if (verifier.Length is < 43 or > 128) return false;
        var computed = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    // ------------------------------------------------------------------ revocation

    /// <summary>RFC 7009: revoke a refresh token (and its access token) or an access token. Unknown tokens are ignored.</summary>
    public async Task RevokeTokenAsync(string? token, string? clientId, string? clientSecret, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return;
        var client = await FindClientAsync(clientId, ct) ?? throw new OAuthException("invalid_client", "Unknown client_id.", 401);
        AuthenticateClient(client, clientSecret);
        var now = clock.GetUtcNow();
        var hash = Secrets.Sha256Hex(token);
        var refresh = await db.OAuthRefreshTokens.FirstOrDefaultAsync(t => t.Hash == hash && t.RevokedAt == null, ct);
        if (refresh is not null)
        {
            refresh.RevokedAt = now;
            await db.Credentials.Where(c => c.Id == refresh.AccessCredentialId && c.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, now), ct);
        }
        else
        {
            await db.Credentials.Where(c => c.Hash == hash && c.Kind == CredentialKinds.Service && c.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, now), ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task RevokeGrantTokensAsync(Guid grantId, DateTimeOffset now, CancellationToken ct)
    {
        var tokens = await db.OAuthRefreshTokens.Where(t => t.GrantId == grantId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) t.RevokedAt = now;
        var credIds = await db.OAuthRefreshTokens.Where(t => t.GrantId == grantId).Select(t => t.AccessCredentialId).ToListAsync(ct);
        await db.Credentials.Where(c => credIds.Contains(c.Id) && c.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, now), ct);
    }

    public Task<OAuthGrant?> GrantForServiceAccountAsync(Guid tenantId, Guid serviceAccountId, CancellationToken ct)
        => db.OAuthGrants.AsNoTracking().FirstOrDefaultAsync(g => g.TenantId == tenantId && g.ServiceAccountId == serviceAccountId && g.RevokedAt == null, ct);

    public Task<OAuthClient?> ClientOfGrantAsync(OAuthGrant grant, CancellationToken ct) => FindClientAsync(grant.ClientId, ct);

    /// <summary>Disconnects an app: revokes the consent and every token, and disables the backing service account.</summary>
    public async Task DisconnectAsync(Guid tenantId, Guid serviceAccountId, Guid? by, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var grants = await db.OAuthGrants.Where(g => g.TenantId == tenantId && g.ServiceAccountId == serviceAccountId && g.RevokedAt == null).ToListAsync(ct);
        foreach (var g in grants)
        {
            g.RevokedAt = now;
            await RevokeGrantTokensAsync(g.Id, now, ct);
            audit.Add(tenantId, AuditActions.OAuthRevoked, serviceAccountId, accountId: by, target: g.ClientId);
        }
        await db.SaveChangesAsync(ct);
        await accounts.SetEnabledAsync(tenantId, serviceAccountId, by, false, ct);
    }
}
