using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Searches navigation routes, tabs, fields, and actions on the current view by keyword.
/// </summary>
public sealed class SearchSettingsHandler : IUiToolHandler
{
    public string ToolName => "search_settings";

    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var query = UiToolArgs.GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return new(JsonSerializer.Serialize(new { ok = false, error = "Search query is required." }), false, null, ctx.CurrentSnapshotJson);

        var res = await ctx.Host.InvokeDriverStringAsync("searchSettings", ct, query);
        return new(res, false, null, ctx.CurrentSnapshotJson);
    }
}
