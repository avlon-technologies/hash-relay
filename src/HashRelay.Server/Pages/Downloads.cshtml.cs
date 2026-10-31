using System.Security.Cryptography;
using HashRelay.Server.Api;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages;

public sealed class DownloadsModel(IWebHostEnvironment env, IOptions<HashRelayOptions> options) : PageModel
{
    public sealed record Artifact(string Href, long Size, string Sha256)
    {
        public string SizeLabel => Size > 1024 * 1024 ? $"{Size / 1024d / 1024d:0.0} MB" : $"{Size / 1024d:0} KB";
    }

    private static readonly Dictionary<string, (Artifact Artifact, DateTime Stamp)> Cache = new();

    public Artifact? Installer { get; private set; }
    public Artifact? CliZip { get; private set; }
    public string Version => ApiEndpoints.Version;
    public string Host => new Uri(PublicUrlHelper.Resolve(options.Value, Request)).Host;

    public void OnGet()
    {
        Installer = Find("HashRelay-Setup.exe");
        CliZip = Find("hash-relay-cli-win-x64.zip");
    }

    private Artifact? Find(string name)
    {
        var path = Path.Combine(env.WebRootPath, "downloads", name);
        if (!System.IO.File.Exists(path)) return null;
        var info = new FileInfo(path);
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var c) && c.Stamp == info.LastWriteTimeUtc) return c.Artifact;
            using var fs = info.OpenRead();
            var sha = Convert.ToHexStringLower(SHA256.HashData(fs));
            var art = new Artifact($"/downloads/{name}", info.Length, sha);
            Cache[name] = (art, info.LastWriteTimeUtc);
            return art;
        }
    }
}
