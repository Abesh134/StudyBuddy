using System.Net;
using System.Text.RegularExpressions;
using Markdig;

namespace StudyBuddy.Services.Documents;

public interface ISafeMarkdownRenderer
{
    string RenderToSafeHtml(string? markdown);
}

/// <summary>
/// Renders structured study Markdown to sanitized, email/browser-safe HTML:
/// - Raw HTML disabled via Markdig DisableHtml()
/// - LaTeX math ($...$, $$...$$, \(...\), \[...\]) protected from Markdown mangling
/// - Responsive scrollable table wrappers
/// - Strips dangerous tags, event handlers, javascript:/data: URLs, and external resource auto-loads
/// </summary>
public sealed partial class SafeMarkdownRenderer : ISafeMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .DisableHtml()
        .Build();

    public string RenderToSafeHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        // 1. Protect LaTeX math spans before Markdig parses underscores/asterisks
        var mathPlaceholders = new Dictionary<string, (string RawLatex, bool IsDisplay)>(StringComparer.Ordinal);
        var counter = 0;

        string ProtectMatch(Match m, bool isDisplay)
        {
            var token = $"SBMATHPLACEHOLDER{counter++}TOKEN";
            mathPlaceholders[token] = (m.Value, isDisplay);
            return token;
        }

        var protectedMd = DisplayDollarMathRegex().Replace(markdown, m => ProtectMatch(m, true));
        protectedMd = DisplayBracketMathRegex().Replace(protectedMd, m => ProtectMatch(m, true));
        protectedMd = InlineParenMathRegex().Replace(protectedMd, m => ProtectMatch(m, false));
        protectedMd = InlineDollarMathRegex().Replace(protectedMd, m => ProtectMatch(m, false));

        // 2. Render Markdown to HTML (raw HTML already disabled)
        var html = Markdown.ToHtml(protectedMd, Pipeline);

        // 3. Sanitize HTML defensively (strip any img/script/iframe/style/event handlers/unsafe hrefs)
        html = DangerousTagBlockRegex().Replace(html, string.Empty);
        html = ExternalOrActiveTagRegex().Replace(html, string.Empty);
        html = EventHandlerAttrRegex().Replace(html, string.Empty);
        html = UnsafeHrefRegex().Replace(html, "href=\"#\"");

        // Enforce safe link attributes on remaining http/https links
        html = AnchorTagRegex().Replace(html, "<a rel=\"noopener noreferrer nofollow\" target=\"_blank\"$1>");

        // Wrap tables in responsive horizontal-scroll container
        html = TableOpenRegex().Replace(html, "<div class=\"sb-table-scroll\" tabindex=\"0\" role=\"region\" aria-label=\"Scrollable table\"><table>");
        html = TableCloseRegex().Replace(html, "</table></div>");

        // 4. Restore protected LaTeX math spans as HTML-encoded text for KaTeX client rendering
        foreach (var kvp in mathPlaceholders)
        {
            var encodedLatex = WebUtility.HtmlEncode(kvp.Value.RawLatex);
            var replacement = kvp.Value.IsDisplay
                ? $"<div class=\"sb-math-display\">{encodedLatex}</div>"
                : $"<span class=\"sb-math-inline\">{encodedLatex}</span>";
            html = html.Replace(kvp.Key, replacement, StringComparison.Ordinal);
        }

        return html;
    }

    [GeneratedRegex(@"\$\$[\s\S]+?\$\$", RegexOptions.Compiled)]
    private static partial Regex DisplayDollarMathRegex();

    [GeneratedRegex(@"\\\[[\s\S]+?\\\]", RegexOptions.Compiled)]
    private static partial Regex DisplayBracketMathRegex();

    [GeneratedRegex(@"\\\([\s\S]+?\\\)", RegexOptions.Compiled)]
    private static partial Regex InlineParenMathRegex();

    [GeneratedRegex(@"(?<!\\)\$(?!\$)(?!\s)([^$\n]+?)(?<!\s)(?<!\\)\$", RegexOptions.Compiled)]
    private static partial Regex InlineDollarMathRegex();

    [GeneratedRegex(@"<\s*(script|style|iframe|object|embed|svg|form)[^>]*>[\s\S]*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DangerousTagBlockRegex();

    [GeneratedRegex(@"<\s*/?\s*(img|video|audio|source|iframe|object|embed|script|style|input|button|textarea|select|form)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ExternalOrActiveTagRegex();

    [GeneratedRegex(@"\s+on[a-z]+\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EventHandlerAttrRegex();

    [GeneratedRegex(@"href\s*=\s*(?:""(?:\s*(?:javascript|vbscript|data):[^""]*)""|'(?:\s*(?:javascript|vbscript|data):[^']*)')", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UnsafeHrefRegex();

    [GeneratedRegex(@"<a\b(?![^>]*rel=)([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AnchorTagRegex();

    [GeneratedRegex(@"<table>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TableOpenRegex();

    [GeneratedRegex(@"</table>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TableCloseRegex();
}

