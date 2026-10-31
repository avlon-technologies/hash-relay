using HashRelay.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Services;

/// <summary>Writes and reads the audit trail for service accounts and credential management. Rows never hold secrets.</summary>
public sealed class AuditService(RelayDbContext db, TimeProvider clock, IHttpContextAccessor http)
{
    public const string SourceMcp = "mcp";
    public const string SourceApi = "api";
    public const string SourceDashboard = "dashboard";

    /// <summary>Adds an audit row to the current unit of work without saving; the caller's SaveChanges commits it.</summary>
    public AuditEvent Add(Guid tenantId, string action, Guid? serviceAccountId = null, Guid? credentialId = null, Guid? accountId = null,
        string? target = null, string? detail = null, string? source = null)
    {
        var ctx = http.HttpContext;
        var row = new AuditEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ServiceAccountId = serviceAccountId,
            CredentialId = credentialId,
            AccountId = accountId,
            Action = action,
            Target = Trim(target, 200),
            Detail = Trim(detail, 500),
            Source = source ?? InferSource(ctx),
            Ip = ctx?.Connection.RemoteIpAddress?.ToString(),
            At = clock.GetUtcNow(),
        };
        db.AuditEvents.Add(row);
        return row;
    }

    public async Task RecordAsync(Guid tenantId, string action, Guid? serviceAccountId = null, Guid? credentialId = null, Guid? accountId = null,
        string? target = null, string? detail = null, string? source = null, CancellationToken ct = default)
    {
        Add(tenantId, action, serviceAccountId, credentialId, accountId, target, detail, source);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<AuditEvent>> ForServiceAccountAsync(Guid tenantId, Guid serviceAccountId, int limit, CancellationToken ct)
        => db.AuditEvents.AsNoTracking().Where(a => a.TenantId == tenantId && a.ServiceAccountId == serviceAccountId)
            .OrderByDescending(a => a.At).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);

    public Task<List<AuditEvent>> RecentAsync(Guid tenantId, int limit, CancellationToken ct)
        => db.AuditEvents.AsNoTracking().Where(a => a.TenantId == tenantId)
            .OrderByDescending(a => a.At).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);

    public static string InferSource(HttpContext? ctx)
    {
        if (ctx is null) return "system";
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)) return SourceMcp;
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase)) return SourceApi;
        return SourceDashboard;
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..(max - 1)] + "…";
}
