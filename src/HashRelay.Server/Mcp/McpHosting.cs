using HashRelay.Contracts;
using HashRelay.Server.Auth;

namespace HashRelay.Server.Mcp;

/// <summary>
/// Splits one process into two hostnames. When an MCP host is configured, that host serves only the MCP endpoint,
/// well-known metadata and health, and the MCP endpoint is not served on any other host. Browser-originated requests
/// to the MCP endpoint are refused unless their Origin is allowed (DNS-rebinding defence required by the MCP spec).
/// </summary>
public static class McpHosting
{
    public const string Path = "/mcp";

    public static IApplicationBuilder UseMcpHostSplit(this IApplicationBuilder app, string? mcpHost, IReadOnlyCollection<string> allowedOrigins)
    {
        var host = string.IsNullOrWhiteSpace(mcpHost) ? null : mcpHost.Trim().ToLowerInvariant();
        var origins = new HashSet<string>(allowedOrigins.Select(o => o.Trim().TrimEnd('/').ToLowerInvariant()).Where(o => o.Length > 0), StringComparer.OrdinalIgnoreCase);
        if (host is not null) origins.Add("https://" + host);

        return app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            var isMcpPath = path.StartsWithSegments(Path);

            if (isMcpPath && ctx.Request.Headers.TryGetValue("Origin", out var origin) && !string.IsNullOrEmpty(origin)
                && !origins.Contains(origin.ToString().TrimEnd('/')))
            {
                await ApiErrors.WriteAsync(ctx, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "This Origin may not call the MCP endpoint.");
                return;
            }

            if (host is not null)
            {
                var onMcpHost = string.Equals(ctx.Request.Host.Host, host, StringComparison.OrdinalIgnoreCase);
                if (onMcpHost)
                {
                    if (path == "/" || !path.HasValue)
                    {
                        await ctx.Response.WriteAsJsonAsync(new { name = "HashRelay MCP", endpoint = $"https://{host}{Path}", transport = "streamable-http", auth = "Authorization: Bearer hr_svc_… (Dashboard → Service accounts)", docs = "/docs#mcp" });
                        return;
                    }
                    if (!(isMcpPath || path.StartsWithSegments("/.well-known") || path.StartsWithSegments("/health") || path == "/healthz"))
                    {
                        await ApiErrors.WriteAsync(ctx, StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Only the MCP endpoint is served on this host.");
                        return;
                    }
                }
                else if (isMcpPath)
                {
                    await ApiErrors.WriteAsync(ctx, StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"The MCP endpoint is served at https://{host}{Path}.");
                    return;
                }
            }
            await next();
        });
    }
}
