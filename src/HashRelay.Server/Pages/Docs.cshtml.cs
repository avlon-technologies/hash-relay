using HashRelay.Server.Pages.Dashboard;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages;

public sealed class DocsModel(IOptions<HashRelayOptions> options) : PageModel
{
    public string PublicUrl => PublicUrlHelper.Resolve(options.Value, Request);
    public string McpUrl => McpUrlHelper.Resolve(options.Value, PublicUrl);
    public int ReplayWindowHours => options.Value.ReplayWindowHours;
    public int RateLimit => options.Value.PublishRateLimitPerMinute;
}
