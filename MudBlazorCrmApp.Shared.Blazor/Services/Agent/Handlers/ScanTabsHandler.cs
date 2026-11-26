using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Lists tabs on the current screen or active dialog.
/// </summary>
public sealed class ScanTabsHandler : IUiToolHandler
{
    public string ToolName => "scan_tabs";

    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var res = await ctx.Host.InvokeDriverStringAsync("scanTabs", ct);
        return new(res, false, null, ctx.CurrentSnapshotJson);
    }
}
