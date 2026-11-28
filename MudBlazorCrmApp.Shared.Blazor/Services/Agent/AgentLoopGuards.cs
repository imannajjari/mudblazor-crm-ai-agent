using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Provides stateless heuristics for client loop detection, predictive budget stop,
/// delete-intent classification, and Auto-mode advisory guards.
/// </summary>
public static partial class AgentLoopGuards
{
    private const decimal PredictiveCostRatio = 0.90m;

    [GeneratedRegex(@"\b(delete|remove|erase|purge|destroy|void)\b|حذف|پاک\s*کردن|حذف\s*کن", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteIntentPattern();

    [GeneratedRegex(@"^(how\b|why\b|what\b|where\b|when\b|which\b|can\s+i\b|explain\b|describe\b|tell\s+me\b|show\s+me\s+how\b|\[how-to|چطور|چگونه|چرا|کجا|توضیح|راهنما|آیا)", RegexOptions.IgnoreCase)]
    private static partial Regex AdviceGoalPattern();

    [GeneratedRegex(@"\b(please\s+do|go\s+ahead\s+and)\b|همین\s*الان|انجامش\s*بده", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitOverridePattern();

    // Advice only: the bare phrase "حالت خودکار" as a factual report must not match; a
    // switch verb must be adjacent (English already requires its verb).
    [GeneratedRegex(@"((switch|change|move|enable|use|set|turn)\b[^.!?\u060C\u061F\n]{0,40}\b(auto|automatic)\b[^.!?\u060C\u061F\n]{0,20}(mode|state|behavio(u)r)?|حالت\s+(خودکار|auto)\b[^.!?\u060C\u061F\n]{0,12}(را\s+)?(انتخاب|تعویض|سوییچ|تغییر|روشن|فعال|بگذار)\s*(کن|کنید|کنم|کنی|نمایید|نمای|بزنید|بزن)|(به|با)\s+حالت\s+(خودکار|auto)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex ModeAdvicePattern();

    [GeneratedRegex(@"[^.!?\u060C\u061F\n]*((switch|change|move|enable|use|set|turn)\b[^.!?\u060C\u061F\n]{0,40}\b(auto|automatic)\b[^.!?\u060C\u061F\n]{0,20}(mode|state|behavio(u)r)?|حالت\s+(خودکار|auto)\b[^.!?\u060C\u061F\n]{0,12}(را\s+)?(انتخاب|تعویض|سوییچ|تغییر|روشن|فعال|بگذار)\s*(کن|کنید|کنم|کنی|نمایید|نمای|بزنید|بزن)|(به|با)\s+حالت\s+(خودکار|auto)\b)[^.!?\u060C\u061F\n]*[.!?\u060C\u061F\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex ModeAdviceSentencePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceCollapsePattern();

    [GeneratedRegex(@"[,;،؛]\s*(?=[.!؟۔]|\s*$)")]
    private static partial Regex DanglingSeparatorPattern();

    public static bool AdvisesModeSwitch(string? summary) =>
        !string.IsNullOrWhiteSpace(summary) && ModeAdvicePattern().IsMatch(summary);

    public static string StripModeAdvice(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return string.Empty;
        var stripped = ModeAdviceSentencePattern().Replace(summary, " ");
        stripped = WhitespaceCollapsePattern().Replace(stripped, " ");
        return DanglingSeparatorPattern().Replace(stripped, "").Trim();
    }

    public static bool IsDeleteTaskGoal(string? goal) =>
        !string.IsNullOrWhiteSpace(goal) && DeleteIntentPattern().IsMatch(goal);

    public static bool IsReadOnlyAdviceGoal(string? goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return false;
        var trimmed = goal.Trim();
        return AdviceGoalPattern().IsMatch(trimmed) && !ExplicitOverridePattern().IsMatch(trimmed);
    }

    public static bool ShouldInjectPredictiveFinalCall(
        int step, int maxSteps,
        int calls, int maxCalls,
        decimal cost, decimal maxCost)
    {
        if (step >= Math.Max(1, maxSteps - 2)) return true;
        if (maxCalls > 1 && calls >= maxCalls - 1) return true;
        if (maxCost > 0m && cost >= maxCost * PredictiveCostRatio) return true;
        return false;
    }

    public static string? DetectLoop(IReadOnlyList<string> recentSignatures)
    {
        var count = recentSignatures.Count;
        if (count >= 3 &&
            string.Equals(recentSignatures[^1], recentSignatures[^2], StringComparison.Ordinal) &&
            string.Equals(recentSignatures[^2], recentSignatures[^3], StringComparison.Ordinal))
        {
            return "[loop guard] You repeated the exact same tool call 3 times in a row. Stop repeating it, inspect what changed or try a different approach, or summarize findings for the user.";
        }

        if (count >= 4 &&
            string.Equals(recentSignatures[^1], recentSignatures[^3], StringComparison.Ordinal) &&
            string.Equals(recentSignatures[^2], recentSignatures[^4], StringComparison.Ordinal) &&
            !string.Equals(recentSignatures[^1], recentSignatures[^2], StringComparison.Ordinal))
        {
            return "[loop guard] Alternating two-step tool loop detected. Break out of the loop and answer or take a distinct next step.";
        }

        return null;
    }
}
