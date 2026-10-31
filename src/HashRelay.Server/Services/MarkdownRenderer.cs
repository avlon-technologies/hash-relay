using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HashRelay.Server.Services;

/// <summary>CommonMark + tables/task lists/autolinks → HTML, with raw HTML disabled and unsafe link schemes stripped.</summary>
public static partial class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <param name="title">When given, a leading level-1 heading with the same text is dropped so the page does not show the title twice.</param>
    public static string ToHtml(string markdown, string? title = null)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        if (title is not null && doc.FirstOrDefault() is HeadingBlock { Level: 1 } h && h.Inline is not null)
        {
            var text = string.Concat(h.Inline.Descendants<LiteralInline>().Select(l => l.Content.ToString())).Trim();
            if (string.Equals(text, title.Trim(), StringComparison.OrdinalIgnoreCase)) doc.Remove(h);
        }
        foreach (var link in doc.Descendants<LinkInline>())
            if (!IsSafeUrl(link.Url)) link.Url = "#";
        foreach (var link in doc.Descendants<AutolinkInline>())
            if (!IsSafeUrl(link.Url)) link.Url = "#";
        using var sw = new StringWriter();
        var renderer = new HtmlRenderer(sw);
        Pipeline.Setup(renderer);
        renderer.Render(doc);
        return sw.ToString();
    }

    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        var u = url.Trim();
        if (u.StartsWith('#') || u.StartsWith('/') || u.StartsWith("./") || u.StartsWith("../")) return true;
        var colon = u.IndexOf(':');
        if (colon < 0) return true;
        var scheme = u[..colon].ToLowerInvariant();
        return scheme is "http" or "https" or "mailto";
    }

    [GeneratedRegex(@"```\s*mermaid[^\n]*\n(?<src>.*?)\n\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MermaidFence();

    /// <summary>The first ```mermaid fenced block in the markdown, if any.</summary>
    public static string? FirstMermaidBlock(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return null;
        var m = MermaidFence().Match(markdown);
        return m.Success ? m.Groups["src"].Value.Trim() : null;
    }

    [GeneratedRegex(@"[*_`~>#\[\]]|!\[[^\]]*\]\([^)]*\)|\]\([^)]*\)")]
    private static partial Regex InlineMarkup();

    /// <summary>A plain-text summary (first paragraph that is not a heading, code or diagram) for the toast body.</summary>
    public static string? Summary(string? markdown, int max)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var text = MermaidFence().Replace(markdown, "");
        text = Regex.Replace(text, @"```.*?```", "", RegexOptions.Singleline);
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var line = block.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('|') || line.StartsWith("---")) continue;
            var plain = InlineMarkup().Replace(line.Replace("\r", ""), "").Replace('\n', ' ');
            plain = Regex.Replace(plain, @"\s+", " ").Trim();
            if (plain.Length == 0) continue;
            return plain.Length <= max ? plain : plain[..(max - 1)].TrimEnd() + "…";
        }
        return null;
    }
}
