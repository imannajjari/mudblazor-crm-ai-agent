using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Scrolls a visible MudTable grid container vertically and refreshes the snapshot.
/// </summary>
public sealed class ScrollGridHandler : IUiToolHandler
{
    public string ToolName => "scroll_grid";

    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var dir = UiToolArgs.GetString(args, "direction");
        if (string.IsNullOrWhiteSpace(dir)) dir = "down";
        var res = await ctx.Host.InvokeDriverStringAsync("scrollGrid", ct, dir);
        var snap = await ctx.Host.CaptureSnapshotAsync(ct);
        return new(JsonSerializer.Serialize(new { result = ctx.Host.TryParseNode(res), snapshot = ctx.Host.TryParseNode(snap) }), false, null, snap);
    }
}
