using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MudBlazorCrmApp.Models;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Provides write-time and read-time bilingual heuristic safety filtering, hashing, and IDF ranking for AgentKnowledge.
/// </summary>
public static class AgentKnowledgeGuard
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex EmailRegex = new(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex PhoneRegex = new(@"(?:\+?\d[\d\s\-().]{7,}\d)", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex FiveDigitsRegex = new(@"(?:\d[\s\-]?){5,}", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex CurrencyRegex = new(@"(?:[$€£¥]\s?\d|\d+(?:[.,]\d{2,3})*\s?(?:USD|EUR|TMN|IRR|تومان|ریال)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex LongTokenRegex = new(@"\b(?=[A-Za-z0-9]{24,}\b)(?=.*[A-Za-z])(?=.*\d)[A-Za-z0-9]+\b", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex CredentialRegex = new(@"(?:\b(?:password|passwd|pwd|secret|api[_-]?key|bearer|token|credential)\b|رمز\s*عبور|کلمه\s*عبور|توکن|کلید\s*محرمانه)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex RecordNumRegex = new(@"(?:\b(?:order|invoice|barcode|customer|ticket|po|serial|lead|deal)\s*#?\s*\d{2,}\b|(?:سفارش|فاکتور|مشتری|تیکت|شماره)\s*#?\s*\d{2,})", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex SnakeCaseRegex = new(@"\b[a-z]+(?:_[a-z0-9]+)+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex InstructionRegex = new(@"(?:\b(?:always|never|ignore|disregard|override|must|do\s+not|don't|without\s+asking|without\s+confirmation|skip\s+(?:confirmation|permission)|click\s+(?:delete|save|ok|yes|confirm)|call\s+finish|system\s+prompt)\b|همیشه|هرگز|نادیده\s*بگیر|حتماً|بدون\s*سوال|بدون\s*تایید|روی\s*حذف\s*کلیک|تایید\s*نکن)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex PanelControlRegex = new(@"(?:\b(?:auto[\s_-]*mode|may[\s_-]*save|allow[\s_-]*this[\s_-]*once|don't[\s_-]*allow|permission[\s_-]*(?:prompt|button|gate|dialog)|agent[\s_-]*panel)\b|حالت\s*خودکار|اجازه\s*همیشه|اجازه\s*این\s*بار|رد\s*کردن|پنل\s*دستیار)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}]{3,}", RegexOptions.Compiled, RegexTimeout);

    public static string NormalizeForFilter(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsControl(ch) || ch is >= '\u200B' and <= '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\uFEFF')
                continue;
            sb.Append(ch);
        }
        return Regex.Replace(sb.ToString().Trim(), @"\s+", " ", RegexOptions.None, RegexTimeout);
    }

    public static string? ValidateWrite(string topic, string fact)
    {
        var cleanTopic = NormalizeForFilter(topic);
        if (string.IsNullOrWhiteSpace(cleanTopic) || cleanTopic.Length > 100)
            return "Topic must be 1 to 100 characters.";
        var clean = NormalizeForFilter(fact);
        if (clean.Length is < 12 or > 300)
            return "Fact must be 12 to 300 characters.";
        if (ContainsSensitiveValue(cleanTopic) || ContainsSensitiveValue(clean))
            return "Fact or topic must not contain sensitive values, credentials, or record numbers.";
        if (!PassesReadFilter(cleanTopic) || !PassesReadFilter(clean))
            return "Fact or topic contains disallowed instruction, tool-name, or panel-control wording.";
        return null;
    }

    public static bool PassesReadFilter(string? text)
    {
        var clean = NormalizeForFilter(text);
        if (string.IsNullOrWhiteSpace(clean)) return false;
        return !SnakeCaseRegex.IsMatch(clean)
            && !InstructionRegex.IsMatch(clean)
            && !PanelControlRegex.IsMatch(clean);
    }

    public static string ComputeFactHash(string fact)
    {
        var normalized = NormalizeForFilter(fact).ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static List<AgentKnowledge> RankFacts(string query, IReadOnlyList<AgentKnowledge> facts, int maxResults)
    {
        var queryTokens = Tokenize(NormalizeForFilter(query));
        if (queryTokens.Count == 0 || facts.Count == 0) return [];

        var validFacts = facts.Where(f => !f.IsRetired && PassesReadFilter(f.Topic) && PassesReadFilter(f.Fact)).ToList();
        if (validFacts.Count == 0) return [];

        var n = validFacts.Count;
        var minShared = Math.Min(2, queryTokens.Count);
        var docFreq = queryTokens.ToDictionary(
            qt => qt,
            qt => validFacts.Count(f => Tokenize(f.Topic).Contains(qt) || Tokenize(f.Fact).Contains(qt)));

        var scored = new List<(AgentKnowledge Fact, double Score)>();
        foreach (var f in validFacts)
        {
            var topicTokens = Tokenize(f.Topic);
            var bodyTokens = Tokenize(f.Fact);
            var shared = 0;
            var topicHit = false;
            var score = 0.0;

            foreach (var qt in queryTokens)
            {
                var inTopic = topicTokens.Contains(qt);
                var inBody = bodyTokens.Contains(qt);
                if (!inTopic && !inBody) continue;
                shared++;
                if (inTopic) topicHit = true;
                var df = docFreq[qt];
                var idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));
                score += idf * (inTopic ? 1.5 : 1.0);
            }

            if (shared >= minShared || topicHit)
                scored.Add((f, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Fact.ConfirmCount)
            .ThenByDescending(x => x.Fact.UseCount)
            .Take(maxResults)
            .Select(x => x.Fact)
            .ToList();
    }

    private static bool ContainsSensitiveValue(string text) =>
        EmailRegex.IsMatch(text) || PhoneRegex.IsMatch(text) || FiveDigitsRegex.IsMatch(text) ||
        CurrencyRegex.IsMatch(text) || LongTokenRegex.IsMatch(text) || CredentialRegex.IsMatch(text) ||
        RecordNumRegex.IsMatch(text);

    private static HashSet<string> Tokenize(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TokenRegex.Matches(text ?? ""))
            set.Add(m.Value.ToLowerInvariant());
        return set;
    }
}
