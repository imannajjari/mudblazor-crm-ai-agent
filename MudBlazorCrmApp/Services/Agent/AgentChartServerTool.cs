using System.Text.Json;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Server tool handler allowing the agent to return a structured inline chart directive (<c>create_chart</c>).
/// </summary>
public sealed class AgentChartServerTool : IServerToolHandler
{
    public string Name => "create_chart";

    public Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var title = args.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
            ? tEl.GetString() ?? "Chart"
            : "Chart";
        if (!args.TryGetProperty("points", out var ptsEl))
            return Task.FromResult(JsonSerializer.Serialize(new { ok = false, error = "Points array is required." }));

        return Task.FromResult(AgentChartService.BuildChartResultJson(title, ptsEl));
    }
}
