using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using HashRelay.Server.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Services;

public sealed class CredentialService(RelayDbContext db, TimeProvider clock, ConnectionRegistry registry, IHubContext<NotificationsHub> hub, ILogger<CredentialService> log)
{
    public static IReadOnlyList<string> NormalizeScopes(IEnumerable<string>? scopes)
    {
        var list = (scopes ?? []).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        if (list.Count == 0) throw ApiException.Validation("Choose at least one permission.", "scopes");
        var bad = list.Except(Scopes.All).ToList();
        if (bad.Count > 0) throw ApiException.Validation($"Unknown permission: {string.Join(", ", bad)}.", "scopes");
        return Scopes.All.Where(list.Contains).ToList();
    }

    public static string ValidateLabel(string? label)
    {
        label = (label ?? "").Trim();
        if (label.Length is < 1 or > Limits.LabelMax) throw ApiException.Validation($"Label must be 1–{Limits.LabelMax} characters.", "label");
        return label;
    }

    /// <summary>Issues a tenant publisher API key. Returns the row and the plaintext secret (shown once, never stored).</summary>
    public async Task<(Credential Credential, string Secret)> IssueApiKeyAsync(Guid tenantId, Guid createdBy, string label, IEnumerable<string> scopes, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var secret = Secrets.NewApiKey();
        var cred = NewCredential(tenantId, CredentialKinds.Api, ValidateLabel(label), NormalizeScopes(scopes), secret, createdBy, null, expiresAt);
        db.Credentials.Add(cred);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Issued API key {CredentialId} for tenant {TenantId}", cred.Id, tenantId);
        return (cred, secret);
    }

    /// <summary>Issues a subscriber token bound to one endpoint, revoking any earlier token for that endpoint.</summary>
    public async Task<(Credential Credential, string Secret)> IssueDeviceTokenAsync(Guid tenantId, Guid? createdBy, Guid endpointRef, string label, IEnumerable<string> scopes, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var previous = await db.Credentials.Where(c => c.TenantId == tenantId && c.Kind == CredentialKinds.Device && c.EndpointRef == endpointRef && c.RevokedAt == null).ToListAsync(ct);
        foreach (var p in previous) p.RevokedAt = now;

        var secret = Secrets.NewDeviceToken();
        var cred = NewCredential(tenantId, CredentialKinds.Device, ValidateLabel(label), NormalizeScopes(scopes), secret, createdBy, endpointRef, null);
        db.Credentials.Add(cred);
        await db.SaveChangesAsync(ct);
        foreach (var p in previous) await NotifyRevokedAsync(p.Id, "replaced", ct);
        return (cred, secret);
    }

    private Credential NewCredential(Guid tenantId, string kind, string label, IReadOnlyList<string> scopes, string secret, Guid? createdBy, Guid? endpointRef, DateTimeOffset? expiresAt)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = kind,
            Label = label,
            Hash = Secrets.Sha256Hex(secret),
            Hint = Secrets.Hint(secret),
            Scopes = string.Join(',', scopes),
            EndpointRef = endpointRef,
            CreatedByAccountId = createdBy,
            CreatedAt = clock.GetUtcNow(),
            ExpiresAt = expiresAt,
        };

    public async Task RevokeAsync(Guid tenantId, Guid credentialId, CancellationToken ct)
    {
        var cred = await db.Credentials.FirstOrDefaultAsync(c => c.Id == credentialId && c.TenantId == tenantId, ct)
                   ?? throw ApiException.NotFound("Credential not found.");
        if (cred.RevokedAt is not null) return;
        cred.RevokedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await NotifyRevokedAsync(cred.Id, "revoked", ct);
        log.LogInformation("Revoked credential {CredentialId} for tenant {TenantId}", cred.Id, tenantId);
    }

    private async Task NotifyRevokedAsync(Guid credentialId, string reason, CancellationToken ct)
    {
        var connections = registry.ConnectionsForCredential(credentialId);
        if (connections.Count == 0) return;
        await hub.Clients.Clients(connections).SendAsync(HubMethods.CredentialRevoked, reason, ct);
    }

    public Task<List<Credential>> ListAsync(Guid tenantId, string kind, CancellationToken ct)
        => db.Credentials.AsNoTracking().Where(c => c.TenantId == tenantId && c.Kind == kind)
            .OrderByDescending(c => c.CreatedAt).ToListAsync(ct);

    public static CredentialInfo ToInfo(Credential c) => new(c.Id, c.Label, c.Kind, c.ScopeList, c.Hint);
}
