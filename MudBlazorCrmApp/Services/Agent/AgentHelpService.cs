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
            var fileTitle = Path.GetFileNameWithoutExtension(file);
            var currentTitle = fileTitle;
            var currentBody = new List<string>();
            var parentTitle = fileTitle;

            foreach (var line in lines)
            {
                var isHeader = false;
                var headerLevel = 0;
                var headerTitle = "";

                if (line.StartsWith("## "))
                {
                    isHeader = true;
                    headerLevel = 2;
                    headerTitle = line[3..].Trim();
                }
                else if (line.StartsWith("### "))
                {
                    isHeader = true;
                    headerLevel = 3;
                    headerTitle = line[4..].Trim();
                }

                if (isHeader)
                {
                    // Save previous section if it has content
                    if (currentBody.Count > 0)
                    {
                        var bodyText = string.Join("\n", currentBody).Trim();
                        if (!string.IsNullOrWhiteSpace(bodyText))
                        {
                            result.Add(new HelpSection(currentTitle, bodyText, Tokenize(currentTitle + " " + bodyText).ToHashSet(StringComparer.OrdinalIgnoreCase)));
                        }
                        currentBody.Clear();
                    }

                    // Update title based on header level
                    if (headerLevel == 2)
                    {
                        // Level 2 (##) becomes new parent
                        parentTitle = headerTitle;
                        currentTitle = headerTitle;
                    }
                    else if (headerLevel == 3)
                    {
                        // Level 3 (###) becomes child with parent prefix
                        currentTitle = $"{parentTitle} > {headerTitle}";
                    }
                }
                else
                {
                    currentBody.Add(line);
                }
            }

            // Save final section
            if (currentBody.Count > 0)
            {
                var bodyText = string.Join("\n", currentBody).Trim();
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    result.Add(new HelpSection(currentTitle, bodyText, Tokenize(currentTitle + " " + bodyText).ToHashSet(StringComparer.OrdinalIgnoreCase)));
                }
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
}
