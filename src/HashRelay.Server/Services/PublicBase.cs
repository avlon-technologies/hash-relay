using Microsoft.Extensions.Options;

namespace HashRelay.Server.Services;

/// <summary>Absolute public origin for links we hand out (detail pages, images): the configured PublicUrl, else the current request's origin.</summary>
public sealed class PublicBase(IHttpContextAccessor http, IOptions<HashRelayOptions> options)
{
    public string Url
    {
        get
        {
            var configured = options.Value.PublicUrl;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
            var req = http.HttpContext?.Request;
            return req is null ? "" : $"{req.Scheme}://{req.Host}";
        }
    }

    public string Absolute(string path) => Url + (path.StartsWith('/') ? path : "/" + path);
}
