using System.Security.Claims;
using HashRelay.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Services;

/// <summary>
/// Resolves the dashboard user's current workspace from their verified session plus membership.
/// The tenant cookie is a preference only; membership is checked on every request.
/// </summary>
public sealed class CurrentTenant(IHttpContextAccessor http, RelayDbContext db)
{
    public const string CookieName = "hr_tenant";
    private bool _loaded;
    private Account? _account;
    private List<Membership> _memberships = [];
    private Membership? _current;

    public static Guid? AccountIdOf(ClaimsPrincipal? user)
        => Guid.TryParse(user?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        _loaded = true;
        var ctx = http.HttpContext;
        var accountId = AccountIdOf(ctx?.User);
        if (ctx is null || accountId is null) return;
        _account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (_account is null) return;
        _memberships = await db.Memberships.AsNoTracking().Include(m => m.Tenant)
            .Where(m => m.AccountId == accountId).OrderBy(m => m.Tenant.CreatedAt).ToListAsync(ct);
        if (_memberships.Count == 0) return;
        Membership? pick = null;
        if (ctx.Request.Cookies.TryGetValue(CookieName, out var raw) && Guid.TryParse(raw, out var preferred))
            pick = _memberships.FirstOrDefault(m => m.TenantId == preferred);
        _current = pick ?? _memberships[0];
    }

    public Account? Account => _account;
    public Tenant? Tenant => _current?.Tenant;
    public Guid TenantId => _current?.TenantId ?? throw new InvalidOperationException("No current workspace.");
    public string Role => _current?.Role ?? Roles.Member;
    public bool CanManage => Roles.CanManageKeys(Role);
    public IReadOnlyList<Membership> Memberships => _memberships;
    public bool HasTenant => _current is not null;

    public void Switch(Guid tenantId)
    {
        var ctx = http.HttpContext ?? throw new InvalidOperationException();
        ctx.Response.Cookies.Append(CookieName, tenantId.ToString(), new CookieOptions
        {
            HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps, Expires = DateTimeOffset.UtcNow.AddYears(1), Path = "/",
        });
    }
}
