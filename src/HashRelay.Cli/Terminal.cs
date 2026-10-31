using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HashRelay.Cli;

/// <summary>Console output helpers: colour (when a TTY), JSON mode and the contract's exit codes.</summary>
public static class Terminal
{
    public static bool Json { get; set; }
    public static bool Quiet { get; set; }
    private static bool _color;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Init()
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR");
        _color = string.IsNullOrEmpty(noColor) && !Console.IsOutputRedirected;
        if (_color && OperatingSystem.IsWindows()) _color = EnableVirtualTerminal();
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
    }

    public const string Reset = "\e[0m";
    public static string Bold(string s) => _color ? $"\e[1m{s}{Reset}" : s;
    public static string Dim(string s) => _color ? $"\e[2m{s}{Reset}" : s;
    public static string Green(string s) => _color ? $"\e[32m{s}{Reset}" : s;
    public static string Red(string s) => _color ? $"\e[31m{s}{Reset}" : s;
    public static string Yellow(string s) => _color ? $"\e[33m{s}{Reset}" : s;
    public static string Cyan(string s) => _color ? $"\e[36m{s}{Reset}" : s;
    public static string Magenta(string s) => _color ? $"\e[35m{s}{Reset}" : s;
    public static string Tag(string tag) => Cyan("#" + tag);

    public static void Line(string s = "") { if (!Json && !Quiet) Console.WriteLine(s); }
    public static void Ok(string s) => Line($"{Green("✓")} {s}");
    public static void Warn(string s) { if (!Json) Console.Error.WriteLine($"{Yellow("!")} {s}"); }
    public static void Info(string s) => Line($"{Dim("·")} {s}");
    public static void Error(string s) { if (!Json) Console.Error.WriteLine($"{Red("✗")} {s}"); }

    public static void Emit(object payload)
    {
        if (Json) Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static int Fail(int code, string message, string? hint = null, object? json = null)
    {
        if (Json) Console.WriteLine(JsonSerializer.Serialize(new { ok = false, exitCode = code, error = message, hint, detail = json }, JsonOptions));
        else
        {
            Error(message);
            if (hint is not null) Console.Error.WriteLine($"  {Dim(hint)}");
        }
        return code;
    }

    public static void KeyValue(string key, string value) => Line($"  {Dim(key.PadRight(12))} {value}");

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static bool EnableVirtualTerminal()
    {
        try
        {
            var handle = GetStdHandle(-11);
            if (!GetConsoleMode(handle, out var mode)) return false;
            return (mode & 0x0004) != 0 || SetConsoleMode(handle, mode | 0x0004);
        }
        catch { return false; }
    }
}

public static class ExitCodes
{
    public const int Ok = 0;
    public const int InvalidInput = 2;
    public const int Authentication = 3;
    public const int ServerUnavailable = 4;
    public const int Rejected = 5;
    public const int Unexpected = 10;
}
