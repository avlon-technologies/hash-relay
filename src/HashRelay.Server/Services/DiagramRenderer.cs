using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Services;

public sealed class DiagramOptions
{
    /// <summary>"mermaid.ink" (default, public service), "kroki" (public or self-hosted), or "none" to skip PNG rendering.</summary>
    public string Provider { get; set; } = "mermaid.ink";
    public string BaseUrl { get; set; } = "https://mermaid.ink";
    public int TimeoutSeconds { get; set; } = 20;
    /// <summary>Viewport width and device scale for crisp toasts on high-DPI screens (mermaid.ink only).</summary>
    public int Width { get; set; } = 800;
    public int Scale { get; set; } = 2;
}

/// <summary>Renders Mermaid source to a PNG. Pluggable because a browser engine does not fit a scale-to-zero container.</summary>
public interface IDiagramRenderer
{
    bool Enabled { get; }
    /// <summary>Returns PNG bytes, or null when rendering failed (the caller publishes without the image).</summary>
    Task<byte[]?> RenderPngAsync(string mermaidSource, CancellationToken ct);
}

public sealed class HttpDiagramRenderer(IHttpClientFactory httpFactory, IOptions<DiagramOptions> options, ILogger<HttpDiagramRenderer> log) : IDiagramRenderer
{
    public const string HttpClientName = "diagrams";

    public bool Enabled => !string.Equals(options.Value.Provider, "none", StringComparison.OrdinalIgnoreCase);

    public async Task<byte[]?> RenderPngAsync(string mermaidSource, CancellationToken ct)
    {
        if (!Enabled) return null;
        var o = options.Value;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 3, 60)));
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            using var res = o.Provider.ToLowerInvariant() switch
            {
                "kroki" => await http.PostAsync($"{o.BaseUrl.TrimEnd('/')}/mermaid/png", new StringContent(mermaidSource, Encoding.UTF8, "text/plain"), cts.Token),
                _ => await http.GetAsync($"{o.BaseUrl.TrimEnd('/')}/img/{Base64Url(Encoding.UTF8.GetBytes(mermaidSource))}?type=png&bgColor=FFFFFF&width={Math.Clamp(o.Width, 300, 2000)}&scale={Math.Clamp(o.Scale, 1, 3)}", cts.Token),
            };
            if (!res.IsSuccessStatusCode)
            {
                var reason = "";
                try { reason = (await res.Content.ReadAsStringAsync(cts.Token)).Trim(); if (reason.Length > 300) reason = reason[..300]; } catch { }
                log.LogWarning("Diagram renderer {Provider} returned {Status}: {Reason}", o.Provider, (int)res.StatusCode, reason);
                return null;
            }
            var bytes = await res.Content.ReadAsByteArrayAsync(cts.Token);
            if (!ImageStore.LooksLike(bytes, "png")) { log.LogWarning("Diagram renderer {Provider} did not return a PNG", o.Provider); return null; }
            return bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Diagram rendering failed via {Provider}", o.Provider);
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');

    /// <summary>Kroki's GET encoding (deflate + base64url), exposed for callers that want shareable links.</summary>
    public static string KrokiEncode(string source)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(Encoding.UTF8.GetBytes(source));
        return Base64Url(ms.ToArray());
    }
}
