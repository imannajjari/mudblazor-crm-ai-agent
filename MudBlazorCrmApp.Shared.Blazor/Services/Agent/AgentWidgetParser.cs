using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public sealed record AgentKpiWidget(string Title, string Value, string? Trend);
public sealed record AgentActionWidget(string Label, string Route);
public sealed record AgentParsedMessage(string MarkdownText, List<AgentKpiWidget> Kpis, List<AgentActionWidget> Actions);

/// <summary>
/// Extracts optional [[kpi|Title|Value|Trend]] and [[action|Label|Route]] mini-widget directives from assistant text.
/// </summary>
public static class AgentWidgetParser
{
    private static readonly Regex DirectiveRegex = new(
        @"\[\[(kpi|action)\|([^\]]+)\]\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static AgentParsedMessage Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new AgentParsedMessage(string.Empty, [], []);

        var kpis = new List<AgentKpiWidget>();
        var actions = new List<AgentActionWidget>();

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
            return m.Value;
        });

        return new AgentParsedMessage(cleaned.Trim(), kpis, actions);
    }

    public static bool IsSafeInternalRoute(string route) =>
        !string.IsNullOrWhiteSpace(route) &&
        route.StartsWith('/') &&
        !route.StartsWith("//", StringComparison.Ordinal) &&
        !route.StartsWith("/\\", StringComparison.Ordinal) &&
        !AgentUiToolDispatcher.IsBlockedRoute(route) &&
        Uri.TryCreate(route, UriKind.Relative, out _);
}
