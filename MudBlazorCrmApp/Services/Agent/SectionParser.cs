using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Parses one markdown help file into flat H2/H3 sections.
/// H2 (##) starts a new top-level section.
/// H3 (###) becomes a child section titled "Parent > Child" so help search
/// returns precise, lower-token chunks instead of one giant section.
/// </summary>
internal sealed partial class SectionParser
{
    private string _parentTitle;
    private string _currentTitle;
    private readonly List<string> _currentBody = new();
    private readonly List<HelpSection> _result = new();

    public SectionParser(string fileTitle)
    {
        _parentTitle = fileTitle;
        _currentTitle = fileTitle;
    }

    public void Feed(string line)
    {
        if (!TryParseHeader(line, out var level, out var title))
        {
            _currentBody.Add(line);
            return;
        }

        FlushBody(); // save the section that just ended under its old title
        _currentTitle = level == 2 ? title : $"{_parentTitle} > {title}";
        if (level == 2)
            _parentTitle = title;
    }

    public void Flush(List<HelpSection> result)
    {
        FlushBody();
        foreach (var section in _result)
            result.Add(section);
    }

    private void FlushBody()
    {
        var bodyText = string.Join("\n", _currentBody).Trim();
        _currentBody.Clear();
        if (!string.IsNullOrWhiteSpace(bodyText))
            _result.Add(new HelpSection(
                _currentTitle,
                bodyText,
                Tokenize(_currentTitle + " " + bodyText).ToHashSet(StringComparer.OrdinalIgnoreCase)));
    }

    private static bool TryParseHeader(string line, out int level, out string title)
    {
        level = 0;
        title = string.Empty;
        if (line.StartsWith("## "))
        {
            level = 2;
            title = line[3..].Trim();
            return true;
        }
        if (line.StartsWith("### "))
        {
            level = 3;
            title = line[4..].Trim();
            return true;
        }
        return false;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match m in TokenRegex().Matches(text))
            if (m.Value.Length >= 2)
                yield return m.Value;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[\p{L}\p{N}_-]+")]
    private static partial System.Text.RegularExpressions.Regex TokenRegex();
}
