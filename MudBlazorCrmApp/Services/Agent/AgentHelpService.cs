using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Services.Agent;

public sealed partial class AgentHelpService : IAgentHelpCatalog
{
    private const double Bm25K1 = 1.5;
    private const double Bm25B = 0.75;

    private readonly List<HelpSection> _sections;
    private readonly Dictionary<string, int> _docFreq = new(StringComparer.OrdinalIgnoreCase);
    private readonly double _avgDocLen;

    public bool IsHealthy { get; private set; }
    public string? HealthError { get; private set; }

    [GeneratedRegex(@"[\p{L}\p{N}_-]+")]
    private static partial Regex TokenRegex();

    public AgentHelpService(IWebHostEnvironment env)
    {
        var docsDir = Path.Combine(env.ContentRootPath, "HelpDocs");
        (_sections, IsHealthy, HealthError) = LoadSections(docsDir);
        if (_sections.Count > 0)
        {
            _avgDocLen = _sections.Average(s => (double)Math.Max(1, s.TermFreqs.Values.Sum()));
            foreach (var sec in _sections)
            {
                foreach (var term in sec.TermFreqs.Keys)
                    _docFreq[term] = _docFreq.GetValueOrDefault(term) + 1;
            }
        }
    }

    public List<HelpSearchHit> Search(string query, int maxResults = 3)
    {
        if (!IsHealthy || string.IsNullOrWhiteSpace(query) || _sections.Count == 0)
            return [];

        var queryTokens = Tokenize(query).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (queryTokens.Count == 0)
            return [];

        var docCount = (double)_sections.Count;
        return _sections
            .Select(sec =>
            {
                var docLen = Math.Max(1.0, sec.TermFreqs.Values.Sum());
                double bm25 = 0.0;
                foreach (var term in queryTokens)
                {
                    if (!sec.TermFreqs.TryGetValue(term, out var tf)) continue;
                    var df = _docFreq.GetValueOrDefault(term, 1);
                    var idf = Math.Log(1.0 + (docCount - df + 0.5) / (df + 0.5));
                    var num = tf * (Bm25K1 + 1.0);
                    var den = tf + Bm25K1 * (1.0 - Bm25B + Bm25B * (docLen / Math.Max(1.0, _avgDocLen)));
                    bm25 += idf * (num / den);
                    if (sec.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) bm25 += 2.0;
                }
                var intScore = (int)Math.Round(bm25 * 100);
                return new { sec, score = intScore };
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(maxResults)
            .Select(x => new HelpSearchHit(x.sec.Title, x.sec.Content, x.score))
            .ToList();
    }

    public HelpSearchHit? GetPage(string titleQuery)
    {
        if (!IsHealthy) return null;
        var matches = _sections
            .Where(s => s.Title.Contains(titleQuery, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return null;
        if (matches.Count == 1) return new HelpSearchHit(matches[0].Title, matches[0].Content, 1);

        var combined = string.Join("\n\n", matches.Select(m => $"### {m.Title}\n{m.Content}"));
        if (combined.Length > 8000) combined = combined[..8000];
        return new HelpSearchHit(matches[0].Title, combined, 1);
    }

    private static (List<HelpSection> Sections, bool Healthy, string? Error) LoadSections(string docsDir)
    {
        var result = new List<HelpSection>();
        if (!Directory.Exists(docsDir)) return (result, false, "HelpDocs directory missing.");

        try
        {
            foreach (var file in Directory.GetFiles(docsDir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var parser = new SectionParser(Path.GetFileNameWithoutExtension(file));
                foreach (var line in File.ReadAllLines(file))
                    parser.Feed(line);
                parser.Flush(result);
            }
            return (result, result.Count > 0, result.Count == 0 ? "No help sections found." : null);
        }
        catch (IOException ex)
        {
            return (result, false, ex.Message);
        }
    }

    internal static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match m in TokenRegex().Matches(text))
            if (m.Value.Length >= 2)
                yield return m.Value;
    }
}
