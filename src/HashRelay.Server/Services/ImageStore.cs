using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HashRelay.Contracts;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Services;

/// <summary>
/// Flat file store for notification images (uploads and rendered diagrams) under the data directory, served anonymously
/// at <c>/i/{name}</c>. Names are unguessable (random or content-hash) so the URL itself is the capability.
/// </summary>
public sealed partial class ImageStore
{
    public const string RoutePrefix = "/i/";
    private readonly string _dir;

    public ImageStore(IOptions<HashRelayOptions> options, IConfiguration config)
    {
        var configured = options.Value.ImagesPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var db = config["HashRelay:DatabasePath"];
            var dataDir = string.IsNullOrWhiteSpace(db) ? Path.Combine(AppContext.BaseDirectory, "data") : Path.GetDirectoryName(Path.GetFullPath(db))!;
            configured = Path.Combine(dataDir, "images");
        }
        _dir = configured;
        Directory.CreateDirectory(_dir);
    }

    public string Directory_ => _dir;

    [GeneratedRegex(@"^[a-z0-9]{8,64}\.(png|jpg|gif|webp)$")]
    private static partial Regex NameRegex();

    public static string? ExtensionFor(string? contentType) => contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" or "image/jpg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => null,
    };

    public static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>Checks the magic bytes match the declared type, so a renamed HTML file is never served as an image.</summary>
    public static bool LooksLike(ReadOnlySpan<byte> bytes, string ext) => ext switch
    {
        "png" => bytes.Length > 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "jpg" => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        "gif" => bytes.Length > 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)),
        "webp" => bytes.Length > 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };

    /// <summary>Stores bytes under a random name and returns it (e.g. <c>3f9c….png</c>).</summary>
    public async Task<string> SaveAsync(ReadOnlyMemory<byte> bytes, string ext, CancellationToken ct)
    {
        var name = $"{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}.{ext}";
        await File.WriteAllBytesAsync(Path.Combine(_dir, name), bytes, ct);
        return name;
    }

    /// <summary>Stores bytes under a content-derived name so identical content is written once; returns the name.</summary>
    public async Task<string> SaveCachedAsync(string cacheKey, ReadOnlyMemory<byte> bytes, string ext, CancellationToken ct)
    {
        var name = CachedName(cacheKey, ext);
        var path = Path.Combine(_dir, name);
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes, ct);
        return name;
    }

    public static string CachedName(string cacheKey, string ext)
        => $"d{Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cacheKey)))[..31]}.{ext}";

    public bool Exists(string name) => NameRegex().IsMatch(name) && File.Exists(Path.Combine(_dir, name));

    /// <summary>Resolves a public name to a file path, or null when the name is malformed or missing.</summary>
    public string? PathFor(string name)
    {
        if (!NameRegex().IsMatch(name)) return null;
        var path = Path.Combine(_dir, name);
        return File.Exists(path) ? path : null;
    }

    public static string PublicPath(string name) => RoutePrefix + name;

    public static int MaxBytes => Limits.ImageMaxBytes;
}
