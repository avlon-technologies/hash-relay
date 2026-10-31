using System.Text.Json;
using System.Text.Json.Serialization;

namespace HashRelay.Client;

/// <summary>Local device configuration shared by the CLI and the Windows agent.</summary>
public sealed class RelayConfig
{
    public const string DefaultServer = "https://hashrelay.avlon.ca";

    public string Server { get; set; } = DefaultServer;
    public string? ApiKey { get; set; }
    public string EndpointId { get; set; } = Guid.NewGuid().ToString("n");
    public string DeviceName { get; set; } = Environment.MachineName;
    public string? TenantName { get; set; }
    public string? TenantSlug { get; set; }
    public string? KeyLabel { get; set; }
    public DateTimeOffset? LoggedInAt { get; set; }

    [JsonIgnore] public bool IsLoggedIn => !string.IsNullOrWhiteSpace(ApiKey);

    public Uri ServerUri => new(Server.TrimEnd('/') + "/", UriKind.Absolute);
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Directory
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("HASHRELAY_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HashRelay");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var baseDir = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg;
            return Path.Combine(baseDir, "hashrelay");
        }
    }

    public static string FilePath => Path.Combine(Directory, "config.json");
    public static string LogDirectory => Path.Combine(Directory, "logs");

    public static bool Exists => File.Exists(FilePath);

    public static RelayConfig Load()
    {
        if (!File.Exists(FilePath)) return new RelayConfig();
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<RelayConfig>(json, Json) ?? new RelayConfig();
        }
        catch (JsonException)
        {
            return new RelayConfig();
        }
    }

    public static void Save(RelayConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
