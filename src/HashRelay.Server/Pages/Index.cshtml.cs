using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages;

public sealed class IndexModel(IOptions<HashRelayOptions> options) : PageModel
{
    public string MachineName => "DESKTOP";
    public string PublicUrl => PublicUrlHelper.Resolve(options.Value, Request);
}

public static class PublicUrlHelper
{
    public static string Resolve(HashRelayOptions options, HttpRequest request)
        => string.IsNullOrWhiteSpace(options.PublicUrl) ? $"{request.Scheme}://{request.Host}" : options.PublicUrl.TrimEnd('/');
}
