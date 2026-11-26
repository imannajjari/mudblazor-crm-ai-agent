using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

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
