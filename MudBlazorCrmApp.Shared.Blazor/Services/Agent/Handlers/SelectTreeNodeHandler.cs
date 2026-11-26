using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Finds and clicks a visible MudTreeView node by label text.
/// </summary>
public sealed class SelectTreeNodeHandler : IUiToolHandler
{
    public string ToolName => "select_tree_node";

    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var label = UiToolArgs.GetString(args, "label");
        if (string.IsNullOrWhiteSpace(label))
            return new(JsonSerializer.Serialize(new { ok = false, error = "Tree node label is required." }), false, null, ctx.CurrentSnapshotJson);

        var res = await ctx.Host.InvokeDriverStringAsync("selectTreeNode", ct, label);
        ctx.Safety.MarkRead();
        var snap = await ctx.Host.CaptureSnapshotAsync(ct);
        return new(JsonSerializer.Serialize(new { result = ctx.Host.TryParseNode(res), snapshot = ctx.Host.TryParseNode(snap) }), false, null, snap);
    }
}
