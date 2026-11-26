using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

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
