namespace HashRelay.Cli;

/// <summary>Tiny argument parser: <c>--flag</c>, <c>--key value</c>, <c>--key=value</c>, repeated options and positionals.</summary>
public sealed class Args
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positionals { get; } = [];

    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "tag", "t", "title", "body", "b", "url", "priority", "p", "id", "key", "server", "name", "limit", "timeout", "scopes",
    };

    public static Args Parse(IEnumerable<string> argv)
    {
        var a = new Args();
        var list = argv.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (arg == "--") { a.Positionals.AddRange(list.Skip(i + 1)); break; }
            if (arg.StartsWith("--") || (arg.Length == 2 && arg[0] == '-' && char.IsLetter(arg[1])))
            {
                var name = arg.TrimStart('-');
                string? value = null;
                var eq = name.IndexOf('=');
                if (eq >= 0) { value = name[(eq + 1)..]; name = name[..eq]; }
                else if (ValueOptions.Contains(name) && i + 1 < list.Count) { value = list[++i]; }
                if (value is null) a._flags.Add(name);
                else { if (!a._options.TryGetValue(name, out var l)) a._options[name] = l = []; l.Add(value); }
            }
            else a.Positionals.Add(arg);
        }
        return a;
    }

    public bool Has(params string[] names) => names.Any(n => _flags.Contains(n) || _options.ContainsKey(n));
    public string? Get(params string[] names) => names.Select(n => _options.TryGetValue(n, out var l) ? l[^1] : null).FirstOrDefault(v => v is not null);
    public IReadOnlyList<string> All(params string[] names) => names.SelectMany(n => _options.TryGetValue(n, out var l) ? l : []).ToList();
    public string? Positional(int i) => i < Positionals.Count ? Positionals[i] : null;
}
