using System.Text.RegularExpressions;

namespace HashRelay.Contracts;

/// <summary>
/// Hashtag normalisation shared by server, CLI and agent so that "#Builds", "builds" and " #BUILDS "
/// all address the same subscription topic inside a tenant. Canonical form (per the Windows M1 contract):
/// invariant lowercase matching <c>^#[a-z0-9][a-z0-9_-]{0,63}$</c>; stored without the leading '#'.
/// </summary>
public static partial class Tags
{
    public const int MaxLength = 64;
    public const int MaxPerPublication = 10;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex ValidTag();

    public static bool TryNormalize(string? raw, out string tag)
    {
        tag = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var t = raw.Trim();
        while (t.StartsWith('#')) t = t[1..];
        t = t.Trim().ToLowerInvariant();
        if (t.Length == 0 || t.Length > MaxLength) return false;
        if (!ValidTag().IsMatch(t)) return false;
        tag = t;
        return true;
    }

    public static string Normalize(string raw)
        => TryNormalize(raw, out var tag) ? tag : throw new ArgumentException($"'{raw}' is not a valid hashtag. Use letters, digits, '_' or '-' (max {MaxLength} chars).", nameof(raw));

    /// <summary>Normalises and de-duplicates, preserving first-seen order.</summary>
    public static IReadOnlyList<string> NormalizeAll(IEnumerable<string?> raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var r in raw)
        {
            var tag = Normalize(r ?? string.Empty);
            if (seen.Add(tag)) list.Add(tag);
        }
        return list;
    }

    public static string Display(string tag) => "#" + tag;
    public static string DisplayAll(IEnumerable<string> tags) => string.Join(' ', tags.Select(Display));
    public static bool LooksLikeTag(string arg) => arg.StartsWith('#') && arg.Length > 1;
}
