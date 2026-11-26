using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public sealed class RunBusinessActionHandler(BusinessToolRegistry registry) : IUiToolHandler
{
    public string ToolName => "run_business_action";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var unsavedErr = ctx.Safety.CheckFinishOrNavAllowed();
        if (unsavedErr != null)
            return new(JsonSerializer.Serialize(new { ok = false, blocked = true, reason = unsavedErr }), false, null, ctx.CurrentSnapshotJson);

        var name = UiToolArgs.GetString(args, "action");
        if (string.IsNullOrWhiteSpace(name))
        {
            var available = registry.List().Select(a => new { a.Name, a.Description });
            return new(JsonSerializer.Serialize(new { ok = false, error = "Action name is required.", available }), false, null, ctx.CurrentSnapshotJson);
        }

        if (!registry.TryGet(name, out var action) || action == null)
        {
            var available = registry.List().Select(a => new { a.Name, a.Description });
            return new(JsonSerializer.Serialize(new { ok = false, error = $"Unknown business action '{name}'.", available }), false, null, ctx.CurrentSnapshotJson);
        }

        var res = await action.ExecuteAsync(args, ctx.Host, ct);
        var snap = await ctx.Host.CaptureSnapshotAsync(ct);
        return new(JsonSerializer.Serialize(new { result = ctx.Host.TryParseNode(res), snapshot = ctx.Host.TryParseNode(snap) }), false, null, snap);
    }
}
