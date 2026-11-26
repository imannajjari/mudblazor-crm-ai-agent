using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public interface IBusinessAction
{
    string Name { get; }
    string Description { get; }
    Task<string> ExecuteAsync(JsonElement args, IAgentUiHost host, CancellationToken ct);
}

/// <summary>
/// WebMCP-inspired extensible registry for domain-specific CRM business actions alongside DOM tools.
/// </summary>
public sealed class BusinessToolRegistry(IEnumerable<IBusinessAction> actions)
{
    private readonly Dictionary<string, IBusinessAction> _map =
        actions.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IBusinessAction> List() => _map.Values;

    public bool TryGet(string name, out IBusinessAction? action) => _map.TryGetValue(name, out action);
}

public sealed class OpenPipelineSummaryAction : IBusinessAction
{
    public string Name => "open_pipeline_summary";
    public string Description => "Navigate to the visual Sales Pipeline board.";
    public async Task<string> ExecuteAsync(JsonElement args, IAgentUiHost host, CancellationToken ct)
    {
        host.NavigateTo("/pipeline");
        await host.InvokeDriverVoidAsync("waitForSettle", ct, 1000);
        return JsonSerializer.Serialize(new { ok = true, navigatedTo = "/pipeline" });
    }
}

public sealed class OpenOverdueTasksAction : IBusinessAction
{
    public string Name => "open_overdue_tasks";
    public string Description => "Navigate to the Kanban task board.";
    public async Task<string> ExecuteAsync(JsonElement args, IAgentUiHost host, CancellationToken ct)
    {
        host.NavigateTo("/KanbanTodoTask");
        await host.InvokeDriverVoidAsync("waitForSettle", ct, 1000);
        return JsonSerializer.Serialize(new { ok = true, navigatedTo = "/KanbanTodoTask" });
    }
}

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
