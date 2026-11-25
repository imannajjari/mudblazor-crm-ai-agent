using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Services.Agent;

public sealed partial class AgentHelpService
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

        var scored = _sections
            .Select(sec =>
            {
                var overlap = queryTokens.Count(t => sec.Tokens.Contains(t));
                var titleBonus = queryTokens.Count(t => sec.Title.Contains(t, StringComparison.OrdinalIgnoreCase)) * 2;
                return new { sec, score = overlap + titleBonus };
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(maxResults)
            .Select(x => new HelpSearchHit(x.sec.Title, x.sec.Content, x.score))
            .ToList();

        return scored;
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
            var lines = File.ReadAllLines(file);
            var currentTitle = Path.GetFileNameWithoutExtension(file);
            var currentBody = new List<string>();

            foreach (var line in lines)
            {
                if (line.StartsWith("## "))
                {
                    if (currentBody.Count > 0)
                    {
                        var bodyText = string.Join("\n", currentBody).Trim();
                        result.Add(new HelpSection(currentTitle, bodyText, Tokenize(currentTitle + " " + bodyText).ToHashSet(StringComparer.OrdinalIgnoreCase)));
                        currentBody.Clear();
                    }
                    currentTitle = line[3..].Trim();
                }
                else
                {
                    currentBody.Add(line);
                }
            }

            if (currentBody.Count > 0)
            {
                var bodyText = string.Join("\n", currentBody).Trim();
                result.Add(new HelpSection(currentTitle, bodyText, Tokenize(currentTitle + " " + bodyText).ToHashSet(StringComparer.OrdinalIgnoreCase)));
            }
        }

        return result;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match m in TokenRegex().Matches(text))
        {
            if (m.Value.Length >= 2)
                yield return m.Value;
        }
    }

    public sealed record HelpSection(string Title, string Content, HashSet<string> Tokens);
    public sealed record HelpSearchHit(string Title, string Content, int Score);
}
