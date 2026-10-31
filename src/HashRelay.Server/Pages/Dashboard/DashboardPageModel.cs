using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages.Dashboard;

/// <summary>Base for dashboard pages: loads the current workspace and bounces to signup when the account has none.</summary>
public abstract class DashboardPageModel(CurrentTenant current, IOptions<HashRelayOptions> options) : PageModel
{
    protected CurrentTenant Current { get; } = current;
    public string PublicUrl => PublicUrlHelper.Resolve(options.Value, Request);
    public string TenantName => Current.Tenant?.Name ?? "";
    public bool CanManage => Current.CanManage;

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        await Current.LoadAsync(HttpContext.RequestAborted);
        if (!Current.HasTenant)
        {
            context.Result = Redirect("/dashboard/settings?nows=1");
            if (!(context.ActionDescriptor.ViewEnginePath?.EndsWith("/Settings", StringComparison.OrdinalIgnoreCase) ?? false)) return;
            context.Result = null;
        }
        await next();
    }

    protected IActionResult Flash(string kind, string message, string? redirect = null)
    {
        TempData["flash.kind"] = kind;
        TempData["flash.message"] = message;
        return Redirect(redirect ?? Request.Path.Value ?? "/dashboard");
    }
}
