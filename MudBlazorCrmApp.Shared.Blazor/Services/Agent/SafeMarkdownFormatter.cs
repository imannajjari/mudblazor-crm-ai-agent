using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Converts basic Markdown (including tables) in agent replies into XSS-safe HTML.
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
        encoded = CodeRegex().Replace(encoded, "<code style=\"background:rgba(128,128,128,0.15);padding:1px 4px;border-radius:4px;direction:ltr;unicode-bidi:embed;\">$1</code>");
        encoded = BoldRegex().Replace(encoded, "<strong>$1</strong>");
        encoded = ItalicRegex().Replace(encoded, "<em>$1</em>");

        var lines = encoded.Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsTableRow(lines[i]) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                i = AppendTableHtml(lines, i, sb);
                continue;
            }

            var line = BulletRegex().Replace(lines[i], "• $1");
            if (line.Trim() == "---")
                sb.Append("<hr style=\"border:none;border-top:1px solid var(--mud-palette-lines-default);margin:8px 0;\" />");
            else
                sb.Append(line);

            if (i < lines.Length - 1) sb.Append("<br />");
        }
        return sb.ToString();
    }

    private static int AppendTableHtml(string[] lines, int startIdx, StringBuilder sb)
    {
        var headers = SplitTableCells(lines[startIdx]);
        sb.Append("<table class=\"agent-md-table\"><thead><tr>");
        foreach (var h in headers) sb.Append("<th>").Append(h).Append("</th>");
        sb.Append("</tr></thead><tbody>");

        var idx = startIdx + 2;
        while (idx < lines.Length && IsTableRow(lines[idx]))
        {
            var cells = SplitTableCells(lines[idx]);
            sb.Append("<tr>");
            for (var c = 0; c < headers.Length; c++)
                sb.Append("<td>").Append(c < cells.Length ? cells[c] : string.Empty).Append("</td>");
            sb.Append("</tr>");
            idx++;
        }
        sb.Append("</tbody></table>");
        return idx - 1;
    }

    private static bool IsTableRow(string line) => line.Trim().StartsWith('|') && line.Trim().EndsWith('|');
    private static bool IsTableSeparator(string line) => IsTableRow(line) && TableSepRegex().IsMatch(line.Trim());
    private static string[] SplitTableCells(string line) =>
        line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    [GeneratedRegex(@"^#{1,3}\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(?:-|\*)\s+(.+)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"\*\*([^\*\n]+)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<!\*)\*([^\*\n]+)\*(?!\*)")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"^\|[\s:\-\|]+\|$")]
    private static partial Regex TableSepRegex();
}
