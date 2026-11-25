using System.Net;
using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Converts basic Markdown in agent replies into XSS-safe HTML (HTML-encoding first).
/// </summary>
public static partial class SafeMarkdownFormatter
{
    public static string ToSafeHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;

        var encoded = WebUtility.HtmlEncode(markdown)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        encoded = HeadingRegex().Replace(encoded, "<strong>$1</strong>");
        encoded = BulletRegex().Replace(encoded, "• $1");
        encoded = CodeRegex().Replace(encoded, "<code style=\"background:rgba(128,128,128,0.15);padding:1px 4px;border-radius:4px;direction:ltr;unicode-bidi:embed;\">$1</code>");
        encoded = BoldRegex().Replace(encoded, "<strong>$1</strong>");
        encoded = ItalicRegex().Replace(encoded, "<em>$1</em>");
        return encoded.Replace("\n", "<br />");
    }

    [GeneratedRegex(@"^#{1,3}\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(?:-|\*)\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"\*\*([^\*\n]+)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<!\*)\*([^\*\n]+)\*(?!\*)")]
    private static partial Regex ItalicRegex();
}
