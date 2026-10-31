using HashRelay.Client;

namespace HashRelay.Cli;

/// <summary>Help pages: one structured document per command, rendered with aligned columns and consistent sections.</summary>
public static class Help
{
    private const string Tagline = "hashtag notifications for your desktop";

    public sealed record Option(string Flags, string Description, string? Arg = null);
    public sealed record Section(string Title, IReadOnlyList<Option> Options, string? Note = null);
    public sealed record Command(
        string Name,
        string Summary,
        string Usage,
        IReadOnlyList<Section> Sections,
        IReadOnlyList<string> Examples,
        IReadOnlyList<string> Notes,
        string Group);

    private static readonly Option[] NoOptions = [];

    public static readonly IReadOnlyList<Command> Commands =
    [
        new("login", "Pair this device with a workspace in your browser",
            "hashrelay login [--key KEY] [--no-browser]",
            [new("Options", [
                new("--key", "Store an API key instead of pairing (headless machines, CI)", "KEY"),
                new("--no-browser", "Print the pairing link instead of opening a browser"),
            ])],
            ["hashrelay login", "hashrelay login --key hr_live_… --server https://relay.example.com"],
            ["Pairing opens the dashboard, shows a short code and waits for you to approve it.",
             "The device token it stores can be revoked any time from Dashboard → Devices."],
            "Get started"),

        new("status", "Show server, workspace, device and agent status",
            "hashrelay status", [], ["hashrelay status", "hashrelay status --json"], [], "Get started"),

        new("open", "Open the dashboard in your browser",
            "hashrelay open", [], ["hashrelay open"], [], "Get started"),

        new("subscribe", "Subscribe this device to one or more hashtags",
            "hashrelay subscribe #tag [#tag…]",
            [new("Options", [new("-t, --tag", "Tags as an option instead of positionals (comma or space separated)", "TAG")])],
            ["hashrelay subscribe #builds", "hashrelay subscribe #builds #deploys #alerts"],
            ["Tags are case-insensitive. The leading # is optional on the command line."],
            "Subscriptions"),

        new("unsubscribe", "Remove hashtag subscriptions from this device",
            "hashrelay unsubscribe #tag [#tag…]",
            [new("Options", [new("-t, --tag", "Tags as an option instead of positionals", "TAG")])],
            ["hashrelay unsubscribe #alerts"], [], "Subscriptions"),

        new("subscriptions", "List this device's subscriptions",
            "hashrelay subscriptions", [], ["hashrelay subscriptions"], [], "Subscriptions"),

        new("publish", "Publish a notification to hashtags in your workspace",
            "hashrelay publish #tag [#tag…] \"Title\" [\"Body\"] [options]",
            [
                new("Options", [
                    new("-t, --tag", "Tags as an option instead of #positionals", "TAG"),
                    new("    --title", "Title (otherwise the first bare positional)", "TEXT"),
                    new("-b, --body", "Body (otherwise the second positional)", "TEXT"),
                    new("    --url", "Link opened when the notification is clicked", "URL"),
                    new("-p, --priority", "low, normal or high (default normal)", "LEVEL"),
                    new("    --id", "Idempotency key: a retry with the same id and payload is not duplicated", "UUID"),
                ]),
                new("Rich content", [
                    new("-m, --markdown", "CommonMark body: tables, task lists and ```mermaid fences", "TEXT"),
                    new("    --markdown-file", "Read the markdown from a file ('-' for stdin)", "PATH"),
                    new("-d, --diagram", "Mermaid diagram, rendered to PNG for the toast and live on the page", "SOURCE"),
                    new("    --diagram-file", "Read the diagram from a file ('-' for stdin)", "PATH"),
                    new("    --image", "PNG, JPEG, GIF or WebP up to 3 MB; a local file is uploaded first", "PATH|URL"),
                ], "Shown on the notification's web page; the toast gets a plain summary and the image."),
            ],
            [
                "hashrelay publish #builds \"Build complete\" \"212 tests passed in 48s\"",
                "hashrelay publish #deploys \"v1.4 is live\" --url https://status.example.com --priority high",
                "hashrelay publish #releases \"Release notes\" --markdown-file notes.md",
                "hashrelay publish #ops \"Pipeline\" --diagram \"graph LR; Build --> Test --> Deploy\"",
            ],
            [],
            "Notifications"),

        new("listen", "Stream this device's notifications to the console",
            "hashrelay listen [#tag…]",
            [new("Options", [new("-t, --tag", "Also subscribe to these tags before listening", "TAG")])],
            ["hashrelay listen", "hashrelay listen #builds --json"],
            ["Runs until Ctrl+C. With --json each notification is printed as one JSON line."],
            "Notifications"),

        new("test", "Send a test notification to this device and verify delivery",
            "hashrelay test", [], ["hashrelay test"],
            ["Subscribes this device to #test if needed, publishes to it and reports what was delivered."],
            "Notifications"),

        new("configure", "Change the server or this device's display name",
            "hashrelay configure [--server URL] [--name NAME]",
            [new("Options", [
                new("    --server", "HashRelay server to use (clears the stored credential if it changes)", "URL"),
                new("    --name", "Display name for this device in the dashboard", "NAME"),
            ])],
            ["hashrelay configure --name \"Mike's laptop\"", "hashrelay configure --server https://relay.example.com"], [], "Setup"),

        new("logout", "Forget the stored credential",
            "hashrelay logout", [], ["hashrelay logout"],
            ["The device token stays listed in the dashboard until you revoke it there."], "Setup"),

        new("agent", "Start, stop or check the Windows tray agent",
            "hashrelay agent start|stop|status", [], ["hashrelay agent status", "hashrelay agent start"], [], "Setup"),
    ];

    private static readonly Option[] GlobalOptions =
    [
        new("    --key", "Use this API key instead of the stored credential", "KEY"),
        new("    --server", "Use this server instead of the configured one", "URL"),
        new("    --json", "Machine-readable output"),
        new("-q, --quiet", "Suppress informational output"),
        new("-h, --help", "Show help for a command"),
        new("-v, --version", "Print the version"),
    ];

    public static Command? Find(string name) => Commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static void PrintOverview()
    {
        Console.WriteLine($"{Terminal.Bold("hashrelay")} {Terminal.Dim(Cli.Version)}  {Terminal.Dim("—")}  {Tagline}");
        Console.WriteLine();
        Heading("Usage");
        Console.WriteLine($"  hashrelay {Terminal.Cyan("<command>")} [options]");
        Console.WriteLine();
        Heading("Commands");
        var width = Commands.Max(c => c.Name.Length) + 2;
        foreach (var group in Commands.GroupBy(c => c.Group))
        {
            Console.WriteLine($"  {Terminal.Dim(group.Key)}");
            foreach (var c in group) Console.WriteLine($"    {Terminal.Cyan(c.Name.PadRight(width))}{c.Summary}");
        }
        Console.WriteLine();
        Heading("Global options");
        Table(GlobalOptions);
        Console.WriteLine();
        Heading("Examples");
        Example("hashrelay login");
        Example("hashrelay subscribe #builds");
        Example("hashrelay publish #builds \"Build complete\" \"212 tests passed\"");
        Console.WriteLine();
        Console.WriteLine(Terminal.Dim($"Run hashrelay <command> --help for details on a command.  Config: {ConfigStore.FilePath}"));
    }

    public static void PrintCommand(Command c)
    {
        Console.WriteLine($"{Terminal.Bold("hashrelay " + c.Name)}  {Terminal.Dim("—")}  {c.Summary}");
        Console.WriteLine();
        Heading("Usage");
        Console.WriteLine("  " + Colorize(c.Usage));
        foreach (var s in c.Sections)
        {
            Console.WriteLine();
            Heading(s.Title, s.Note);
            Table(s.Options);
        }
        if (c.Examples.Count > 0)
        {
            Console.WriteLine();
            Heading("Examples");
            foreach (var e in c.Examples) Example(e);
        }
        if (c.Notes.Count > 0)
        {
            Console.WriteLine();
            Heading("Notes");
            foreach (var n in c.Notes) foreach (var line in Wrap(n, Width() - 2)) Console.WriteLine("  " + line);
        }
        Console.WriteLine();
        Console.WriteLine(Terminal.Dim("Global: --key KEY, --server URL, --json, --quiet.  See hashrelay --help."));
    }

    // ---------------------------------------------------------------- rendering

    private static void Heading(string title, string? note = null)
        => Console.WriteLine(Terminal.Bold(Terminal.Yellow(title)) + (note is null ? "" : "  " + Terminal.Dim(note)));

    /// <summary>Two aligned columns: flags (with their argument placeholder) and a wrapped description.</summary>
    private static void Table(IReadOnlyList<Option> options)
    {
        if (options.Count == 0) return;
        var left = options.Select(o => o.Arg is null ? o.Flags : $"{o.Flags} {o.Arg}").ToList();
        var width = left.Max(l => l.Length) + 3;
        var descWidth = Math.Max(30, Width() - 2 - width);
        for (var i = 0; i < options.Count; i++)
        {
            var o = options[i];
            var flags = Terminal.Cyan(o.Flags) + (o.Arg is null ? "" : " " + Terminal.Dim(o.Arg));
            var pad = new string(' ', width - left[i].Length);
            var lines = Wrap(o.Description, descWidth);
            Console.WriteLine($"  {flags}{pad}{lines[0]}");
            foreach (var extra in lines.Skip(1)) Console.WriteLine(new string(' ', 2 + width) + extra);
        }
    }

    private static void Example(string command) => Console.WriteLine($"  {Terminal.Dim("$")} {Colorize(command)}");

    /// <summary>Highlights #tags and --options inside a command line so examples read like the real thing.</summary>
    private static string Colorize(string command)
    {
        var parts = command.Split(' ');
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.StartsWith('#')) parts[i] = Terminal.Cyan(p);
            else if (System.Text.RegularExpressions.Regex.IsMatch(p, "^(--[a-z][a-z-]*|-[a-z])$")) parts[i] = Terminal.Green(p);
            else if (p.StartsWith('[') || p.StartsWith('<')) parts[i] = Terminal.Dim(p);
        }
        return string.Join(' ', parts);
    }

    private static int Width()
    {
        try { return Console.IsOutputRedirected ? 100 : Math.Clamp(Console.WindowWidth, 60, 120); }
        catch { return 100; }
    }

    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width) { lines.Add(line); line = word; }
            else line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }
}
