using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Reads items from an open right-click / context menu popover.
/// </summary>
public sealed class ScanContextMenuHandler : IUiToolHandler
{
    public string ToolName => "scan_context_menu";

    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var res = await ctx.Host.InvokeDriverStringAsync("scanContextMenu", ct);
        return new(res, false, null, ctx.CurrentSnapshotJson);
    }
}
