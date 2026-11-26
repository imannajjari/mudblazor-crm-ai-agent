namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public sealed record AgentParsedMessage(string MarkdownText, List<AgentKpiWidget> Kpis, List<AgentActionWidget> Actions, List<AgentChartWidget> Charts);
