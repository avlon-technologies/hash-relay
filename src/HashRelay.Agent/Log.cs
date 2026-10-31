using HashRelay.Client;

namespace HashRelay.Agent;

/// <summary>Tiny rolling file log under %APPDATA%\HashRelay\logs.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string Path => System.IO.Path.Combine(ConfigStore.LogDirectory, $"agent-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ConfigStore.LogDirectory);
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
                Prune();
            }
        }
        catch { /* logging must never crash the agent */ }
    }

    private static DateTime _lastPrune;
    private static void Prune()
    {
        if ((DateTime.Now - _lastPrune).TotalHours < 6) return;
        _lastPrune = DateTime.Now;
        foreach (var f in new DirectoryInfo(ConfigStore.LogDirectory).GetFiles("agent-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-7)))
            try { f.Delete(); } catch { }
    }
}
