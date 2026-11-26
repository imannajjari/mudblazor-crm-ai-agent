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

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match m in TokenRegex().Matches(text))
            if (m.Value.Length >= 2)
                yield return m.Value;
    }
}
