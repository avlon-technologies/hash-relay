using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages.Dashboard;

public sealed class ServiceAccountsModel(CurrentTenant current, IOptions<HashRelayOptions> options, ServiceAccountService accounts, RelayDbContext db, TimeProvider clock)
    : DashboardPageModel(current, options)
{
    public sealed record Row(ServiceAccount Account, int ActiveKeys);

    public List<Row> Accounts { get; private set; } = [];
    public bool OpenCreate { get; private set; }
    public string McpUrl => McpUrlHelper.Resolve(options.Value, PublicUrl);

    public async Task OnGetAsync(bool create, CancellationToken ct)
    {
        OpenCreate = create && CanManage;
        var list = await accounts.ListAsync(Current.TenantId, ct);
        var ids = list.Select(a => a.Id).ToList();
        var keys = await db.Credentials.AsNoTracking()
            .Where(c => c.TenantId == Current.TenantId && c.Kind == CredentialKinds.Service && c.RevokedAt == null && c.ServiceAccountId != null && ids.Contains(c.ServiceAccountId.Value))
            .GroupBy(c => c.ServiceAccountId!.Value).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        Accounts = list.Select(a => new Row(a, keys.GetValueOrDefault(a.Id))).ToList();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name, string? description, string[] scopes, string allowedTags, string? allowedUrlHosts, int hourlyLimit, int expiresDays, string? publisherType, CancellationToken ct)
    {
        if (!CanManage) return Flash("danger", "Only workspace owners and admins can create service accounts.");
        try
        {
            var draft = ServiceAccountService.Validate(name, description, scopes, allowedTags, allowedUrlHosts, hourlyLimit, publisherType);
            var account = await accounts.CreateAsync(Current.TenantId, Current.Account!.Id, draft, ct);
            var expires = expiresDays > 0 ? clock.GetUtcNow().AddDays(expiresDays) : (DateTimeOffset?)null;
            var (cred, secret) = await accounts.IssueKeyAsync(Current.TenantId, account.Id, Current.Account!.Id, account.Name, expires, ct);
            TempData["svckey.secret"] = secret;
            TempData["svckey.label"] = cred.Label;
            return Redirect($"/dashboard/service-accounts/{account.Id}");
        }
        catch (ApiException ex)
        {
            return Flash("danger", ex.Message, "/dashboard/service-accounts?create=1");
        }
    }
}

/// <summary>Where agents connect: the dedicated MCP host when configured, otherwise /mcp on the public URL.</summary>
public static class McpUrlHelper
{
    public static string Resolve(HashRelayOptions options, string publicUrl)
        => string.IsNullOrWhiteSpace(options.McpHost) ? publicUrl.TrimEnd('/') + "/mcp" : $"https://{options.McpHost.Trim()}/mcp";
}
