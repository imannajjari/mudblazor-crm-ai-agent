using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Services.Agent;

public sealed partial class AgentHelpService : IAgentHelpCatalog
{
    private readonly List<HelpSection> _sections;

    [GeneratedRegex(@"[\p{L}\p{N}_-]+")]
    private static partial Regex TokenRegex();

    public AgentHelpService(IWebHostEnvironment env)
    {
        var docsDir = Path.Combine(env.ContentRootPath, "HelpDocs");
        _sections = LoadSections(docsDir);
    }

    public List<HelpSearchHit> Search(string query, int maxResults = 3)
    {
        if (string.IsNullOrWhiteSpace(query) || _sections.Count == 0)
            return [];

        var queryTokens = Tokenize(query).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (queryTokens.Count == 0)
            return [];

        return _sections
            .Select(sec => new
            {
                sec,
                score = queryTokens.Count(t => sec.Tokens.Contains(t))
                      + queryTokens.Count(t => sec.Title.Contains(t, StringComparison.OrdinalIgnoreCase)) * 2
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(maxResults)
            .Select(x => new HelpSearchHit(x.sec.Title, x.sec.Content, x.score))
            .ToList();
    }

    public HelpSearchHit? GetPage(string titleQuery)
    {
        var sec = _sections.FirstOrDefault(s =>
            s.Title.Contains(titleQuery, StringComparison.OrdinalIgnoreCase));
        return sec == null ? null : new HelpSearchHit(sec.Title, sec.Content, 1);
    }

    private static List<HelpSection> LoadSections(string docsDir)
    {
        var result = new List<HelpSection>();
        if (!Directory.Exists(docsDir)) return result;

        foreach (var file in Directory.GetFiles(docsDir, "*.md"))
        {
            var parser = new SectionParser(Path.GetFileNameWithoutExtension(file));
            foreach (var line in File.ReadAllLines(file))
                parser.Feed(line);
            parser.Flush(result);
        }

        return result;
    }

    /// <summary>
    /// Parses one markdown help file into flat H2/H3 sections.
    /// H2 (##) starts a new top-level section.
    /// H3 (###) becomes a child section titled "Parent > Child" so help search
    /// returns precise, lower-token chunks instead of one giant section.
    /// </summary>
    private sealed class SectionParser
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
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match m in TokenRegex().Matches(text))
            if (m.Value.Length >= 2)
                yield return m.Value;
    }

    public sealed record HelpSection(string Title, string Content, HashSet<string> Tokens);
}
