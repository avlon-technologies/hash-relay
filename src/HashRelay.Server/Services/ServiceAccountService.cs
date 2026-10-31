using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Services;

/// <summary>
/// Service accounts: named non-human principals (AI agents, automations) with fine-grained scopes, a hashtag allowlist,
/// a URL policy and a quota. Their keys are credentials of kind "service"; everything here is audited.
/// </summary>
public sealed class ServiceAccountService(RelayDbContext db, TimeProvider clock, CredentialService credentials, AuditService audit, ILogger<ServiceAccountService> log)
{
    public const int MaxPatterns = 20;
    public const int MaxHosts = 20;
    public const int MinHourlyLimit = 1;
    public const int MaxHourlyLimit = 10_000;
    public const string DefaultAllowedTags = "agent-*";

    public sealed record Draft(string Name, string? Description, IReadOnlyList<string> Scopes, IReadOnlyList<string> AllowedTags, IReadOnlyList<string> AllowedUrlHosts, int HourlyPublishLimit);

    // ------------------------------------------------------------------ validation

    public static Draft Validate(string? name, string? description, IEnumerable<string>? scopes, string? allowedTags, string? allowedUrlHosts, int? hourlyLimit)
    {
        var details = new Dictionary<string, string[]>();
        name = (name ?? "").Trim();
        if (name.Length is < 2 or > Limits.LabelMax) details["name"] = [$"Name must be 2–{Limits.LabelMax} characters."];
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (description is { Length: > 300 }) details["description"] = ["Description must be at most 300 characters."];

        IReadOnlyList<string> scopeList = [];
        try { scopeList = NormalizeScopes(scopes); } catch (ApiException ex) { details["scopes"] = [ex.Message]; }

        IReadOnlyList<string> tags = [];
        try { tags = NormalizePatterns(allowedTags); } catch (ApiException ex) { details["allowedTags"] = [ex.Message]; }

        IReadOnlyList<string> hosts = [];
        try { hosts = NormalizeHosts(allowedUrlHosts); } catch (ApiException ex) { details["allowedUrlHosts"] = [ex.Message]; }

        var limit = hourlyLimit ?? 60;
        if (limit is < MinHourlyLimit or > MaxHourlyLimit) details["hourlyLimit"] = [$"Hourly limit must be between {MinHourlyLimit} and {MaxHourlyLimit}."];

        if (details.Count > 0) throw new ApiException(400, ErrorCodes.Validation, details.Values.First()[0], details);
        return new Draft(name, description, scopeList, tags, hosts, limit);
    }

    public static IReadOnlyList<string> NormalizeScopes(IEnumerable<string>? scopes)
    {
        var list = (scopes ?? []).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        if (list.Count == 0) throw ApiException.Validation("Choose at least one permission.", "scopes");
        var bad = list.Except(ServiceScopes.All).ToList();
        if (bad.Count > 0) throw ApiException.Validation($"Unknown permission: {string.Join(", ", bad)}.", "scopes");
        return ServiceScopes.All.Where(list.Contains).ToList();
    }

    /// <summary>Hashtag glob patterns: comma, space or newline separated, optional leading '#', case-insensitive, '*' wildcard.</summary>
    public static IReadOnlyList<string> NormalizePatterns(string? raw)
    {
        var patterns = (raw ?? "").Split([',', ' ', '\n', '\r', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.TrimStart('#').ToLowerInvariant()).Where(p => p.Length > 0).Distinct().ToList();
        if (patterns.Count == 0) throw ApiException.Validation("Add at least one allowed hashtag pattern (use * to allow every hashtag).", "allowedTags");
        if (patterns.Count > MaxPatterns) throw ApiException.Validation($"At most {MaxPatterns} hashtag patterns.", "allowedTags");
        foreach (var p in patterns)
        {
            if (p.Length > 64 || p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '*')))
                throw ApiException.Validation($"'{p}' is not a valid hashtag pattern. Use letters, digits, '-', '_', '.', '/', ':' and '*'.", "allowedTags");
        }
        return patterns;
    }

    public static IReadOnlyList<string> NormalizeHosts(string? raw)
    {
        var hosts = (raw ?? "").Split([',', ' ', '\n', '\r', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.ToLowerInvariant().TrimEnd('/')).Select(h => h.StartsWith("https://") ? h[8..] : h.StartsWith("http://") ? h[7..] : h)
            .Where(h => h.Length > 0).Distinct().ToList();
        if (hosts.Count > MaxHosts) throw ApiException.Validation($"At most {MaxHosts} allowed hosts.", "allowedUrlHosts");
        foreach (var h in hosts)
        {
            if (Uri.CheckHostName(h) is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
                throw ApiException.Validation($"'{h}' is not a valid host name.", "allowedUrlHosts");
        }
        return hosts;
    }

    // ------------------------------------------------------------------ accounts

    public Task<List<ServiceAccount>> ListAsync(Guid tenantId, CancellationToken ct)
        => db.ServiceAccounts.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.Name).ToListAsync(ct);

    public Task<ServiceAccount?> GetAsync(Guid tenantId, Guid id, CancellationToken ct)
        => db.ServiceAccounts.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == id, ct);

    public async Task<ServiceAccount> RequireAsync(Guid tenantId, Guid id, CancellationToken ct)
        => await db.ServiceAccounts.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == id, ct) ?? throw ApiException.NotFound("Service account not found.");

    public async Task<ServiceAccount> CreateAsync(Guid tenantId, Guid? createdBy, Draft draft, CancellationToken ct)
    {
        if (await db.ServiceAccounts.AnyAsync(s => s.TenantId == tenantId && s.Name == draft.Name, ct))
            throw ApiException.Conflict("A service account with that name already exists.");
        var now = clock.GetUtcNow();
        var account = new ServiceAccount
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = draft.Name, Description = draft.Description,
            Scopes = string.Join(',', draft.Scopes), AllowedTags = string.Join(',', draft.AllowedTags),
            AllowedUrlHosts = draft.AllowedUrlHosts.Count == 0 ? null : string.Join(',', draft.AllowedUrlHosts),
            HourlyPublishLimit = draft.HourlyPublishLimit, Enabled = true, CreatedByAccountId = createdBy, CreatedAt = now, UpdatedAt = now,
        };
        db.ServiceAccounts.Add(account);
        audit.Add(tenantId, AuditActions.ServiceAccountCreated, account.Id, accountId: createdBy, target: account.Name, detail: Describe(account));
        await db.SaveChangesAsync(ct);
        log.LogInformation("Service account {ServiceAccountId} ({Name}) created in tenant {TenantId}", account.Id, account.Name, tenantId);
        return account;
    }

    public async Task<ServiceAccount> UpdateAsync(Guid tenantId, Guid id, Guid? updatedBy, Draft draft, CancellationToken ct)
    {
        var account = await RequireAsync(tenantId, id, ct);
        if (account.Name != draft.Name && await db.ServiceAccounts.AnyAsync(s => s.TenantId == tenantId && s.Name == draft.Name && s.Id != id, ct))
            throw ApiException.Conflict("A service account with that name already exists.");
        account.Name = draft.Name;
        account.Description = draft.Description;
        account.Scopes = string.Join(',', draft.Scopes);
        account.AllowedTags = string.Join(',', draft.AllowedTags);
        account.AllowedUrlHosts = draft.AllowedUrlHosts.Count == 0 ? null : string.Join(',', draft.AllowedUrlHosts);
        account.HourlyPublishLimit = draft.HourlyPublishLimit;
        account.UpdatedAt = clock.GetUtcNow();
        // Keys inherit the account's policy at authentication time, so the stored scope string is kept in step for display.
        await db.Credentials.Where(c => c.ServiceAccountId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Scopes, account.Scopes), ct);
        audit.Add(tenantId, AuditActions.ServiceAccountUpdated, account.Id, accountId: updatedBy, target: account.Name, detail: Describe(account));
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task SetEnabledAsync(Guid tenantId, Guid id, Guid? changedBy, bool enabled, CancellationToken ct)
    {
        var account = await RequireAsync(tenantId, id, ct);
        if (account.Enabled == enabled) return;
        account.Enabled = enabled;
        account.UpdatedAt = clock.GetUtcNow();
        audit.Add(tenantId, enabled ? AuditActions.ServiceAccountEnabled : AuditActions.ServiceAccountDisabled, account.Id, accountId: changedBy, target: account.Name);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Service account {ServiceAccountId} {State}", account.Id, enabled ? "enabled" : "disabled");
    }

    // ------------------------------------------------------------------ keys

    public Task<List<Credential>> KeysAsync(Guid tenantId, Guid id, CancellationToken ct)
        => db.Credentials.AsNoTracking().Where(c => c.TenantId == tenantId && c.Kind == CredentialKinds.Service && c.ServiceAccountId == id)
            .OrderByDescending(c => c.CreatedAt).ToListAsync(ct);

    public Task<int> ActiveKeyCountAsync(Guid tenantId, Guid id, CancellationToken ct)
        => db.Credentials.CountAsync(c => c.TenantId == tenantId && c.Kind == CredentialKinds.Service && c.ServiceAccountId == id && c.RevokedAt == null, ct);

    /// <summary>Issues a key for the account. Returns the row and the plaintext secret, which is shown once and never stored.</summary>
    public async Task<(Credential Credential, string Secret)> IssueKeyAsync(Guid tenantId, Guid id, Guid? createdBy, string? label, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var account = await RequireAsync(tenantId, id, ct);
        label = CredentialService.ValidateLabel(string.IsNullOrWhiteSpace(label) ? account.Name : label);
        var secret = Secrets.NewServiceKey();
        var cred = new Credential
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Kind = CredentialKinds.Service, Label = label,
            Hash = Secrets.Sha256Hex(secret), Hint = Secrets.Hint(secret), Scopes = account.Scopes, ServiceAccountId = account.Id,
            CreatedByAccountId = createdBy, CreatedAt = clock.GetUtcNow(), ExpiresAt = expiresAt,
        };
        db.Credentials.Add(cred);
        audit.Add(tenantId, AuditActions.KeyIssued, account.Id, cred.Id, createdBy, target: cred.Hint, detail: expiresAt is null ? $"{label}, no expiry" : $"{label}, expires {expiresAt:yyyy-MM-dd}");
        await db.SaveChangesAsync(ct);
        log.LogInformation("Issued service key {CredentialId} for service account {ServiceAccountId}", cred.Id, account.Id);
        return (cred, secret);
    }

    public async Task RevokeKeyAsync(Guid tenantId, Guid id, Guid credentialId, Guid? revokedBy, CancellationToken ct)
    {
        var cred = await db.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == credentialId && c.TenantId == tenantId && c.ServiceAccountId == id, ct)
                   ?? throw ApiException.NotFound("Key not found.");
        await credentials.RevokeAsync(tenantId, credentialId, ct);
        await audit.RecordAsync(tenantId, AuditActions.KeyRevoked, id, credentialId, revokedBy, target: cred.Hint, detail: cred.Label, ct: ct);
    }

    /// <summary>Touches LastUsedAt at most once a minute (called from authentication).</summary>
    public async Task TouchAsync(Guid id, DateTimeOffset now, DateTimeOffset? lastUsed, CancellationToken ct)
    {
        if (lastUsed is not null && now - lastUsed < TimeSpan.FromMinutes(1)) return;
        try { await db.ServiceAccounts.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastUsedAt, now), ct); }
        catch (DbUpdateException) { /* non-critical */ }
    }

    public static string Describe(ServiceAccount a)
        => $"scopes {string.Join(' ', a.ScopeList)}; tags {string.Join(' ', a.AllowedTagList)}; hosts {(a.AllowedUrlHostList.Count == 0 ? "any https" : string.Join(' ', a.AllowedUrlHostList))}; {a.HourlyPublishLimit}/hour";
}
