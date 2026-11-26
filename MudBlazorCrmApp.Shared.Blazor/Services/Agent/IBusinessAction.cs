using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public interface IBusinessAction
{
    string Name { get; }
    string Description { get; }
    Task<string> ExecuteAsync(JsonElement args, IAgentUiHost host, CancellationToken ct);
}
