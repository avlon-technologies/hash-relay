using System.Security.Cryptography;
using System.Text;
using HashRelay.Client;

namespace HashRelay.Agent;

/// <summary>
/// Downloads notification images to a local cache so toasts can reference them by file path (Windows does not fetch
/// web images for unpackaged apps). Bounded by size and time; failures just mean a text-only toast.
/// </summary>
internal static class ImageCache
{
    private const int MaxBytes = 5 * 1024 * 1024;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    public static string Directory_ => Path.Combine(ConfigStore.Directory, "cache");

    static ImageCache()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"hash-relay-agent/{AgentInfo.Version}");
    }

    /// <summary>Returns a local file path for the image, or null when it could not be fetched or is not an image.</summary>
    public static async Task<string?> GetAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        try
        {
            Directory.CreateDirectory(Directory_);
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24];
            var existing = Directory.EnumerateFiles(Directory_, key + ".*").FirstOrDefault();
            if (existing is not null) { File.SetLastWriteTimeUtc(existing, DateTime.UtcNow); return existing; }

            using var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return null;
            var ext = res.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
            {
                "image/png" => "png", "image/jpeg" => "jpg", "image/gif" => "gif", "image/webp" => "webp", _ => null,
            };
            if (ext is null || res.Content.Headers.ContentLength is > MaxBytes) return null;
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + read > MaxBytes) return null;
                ms.Write(buffer, 0, read);
            }
            if (ms.Length == 0) return null;
            var path = Path.Combine(Directory_, $"{key}.{ext}");
            await File.WriteAllBytesAsync(path, ms.ToArray(), ct);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn($"Image download failed for {url}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Drops cached images not used for a week. Called once at startup.</summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Directory_)) return;
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(Directory_))
                if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
        }
        catch { /* best effort */ }
    }
}
