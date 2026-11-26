using System.Globalization;
using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Extracts optional [[kpi|Title|Value|Trend]], [[action|Label|Route]], and [[chart|Title|label:val,...]] mini-widget directives from assistant text.
/// </summary>
public static class AgentWidgetParser
{
    private static readonly Regex DirectiveRegex = new(
        @"\[\[(kpi|action|chart)\|([^\]]+)\]\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static AgentParsedMessage Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new AgentParsedMessage(string.Empty, [], [], []);

        var kpis = new List<AgentKpiWidget>();
        var actions = new List<AgentActionWidget>();
        var charts = new List<AgentChartWidget>();

        var cleaned = DirectiveRegex.Replace(raw, m =>
        {
            var kind = m.Groups[1].Value.ToLowerInvariant();
            var parts = m.Groups[2].Value.Split('|').Select(p => p.Trim()).ToArray();
            if (kind == "kpi" && parts.Length >= 2)
            {
                kpis.Add(new AgentKpiWidget(parts[0], parts[1], parts.Length > 2 ? parts[2] : null));
                return string.Empty;
            }
            if (kind == "action" && parts.Length >= 2 && IsSafeInternalRoute(parts[1]))
            {
                actions.Add(new AgentActionWidget(parts[0], parts[1]));
                return string.Empty;
            }
            if (kind == "chart" && parts.Length >= 2 && TryParseChart(parts[0], parts[1], out var chart))
            {
                charts.Add(chart);
                return string.Empty;
            }
            return m.Value;
        });

        return new AgentParsedMessage(cleaned.Trim(), kpis, actions, charts);
    }

    private static bool TryParseChart(string title, string seriesSpec, out AgentChartWidget chart)
    {
        var rawPairs = seriesSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entries = new List<(string Label, double Value)>();
        foreach (var pair in rawPairs.Take(12))
        {
            var colon = pair.LastIndexOf(':');
            if (colon <= 0 || colon >= pair.Length - 1) continue;
            var label = pair[..colon].Trim();
            if (double.TryParse(pair[(colon + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var val)
                && !double.IsNaN(val) && !double.IsInfinity(val))
            {
                entries.Add((label, val));
            }
        }
        if (entries.Count == 0)
        {
            chart = new AgentChartWidget(string.Empty, []);
            return false;
        }
        var max = Math.Max(1.0, entries.Max(e => Math.Max(0.0, e.Value)));
        chart = new AgentChartWidget(title, entries.Select(e => new AgentChartBar(e.Label, e.Value, Math.Clamp(Math.Round(Math.Max(0.0, e.Value) / max * 100.0, 1), 0.0, 100.0))).ToList());
        return true;
    }

    public static bool IsSafeInternalRoute(string route) =>
        !string.IsNullOrWhiteSpace(route) &&
        route.StartsWith('/') &&
        !route.StartsWith("//", StringComparison.Ordinal) &&
        !route.StartsWith("/\\", StringComparison.Ordinal) &&
        !AgentUiToolDispatcher.IsBlockedRoute(route) &&
        Uri.TryCreate(route, UriKind.Relative, out _);
}
