using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Lists navigation menu routes and open menu triggers without navigating away.
/// </summary>
public sealed class ScanMenusHandler : IUiToolHandler
{
    public string ToolName => "scan_menus";

    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var res = await ctx.Host.InvokeDriverStringAsync("scanMenus", ct);
        return new(res, false, null, ctx.CurrentSnapshotJson);
    }
}
