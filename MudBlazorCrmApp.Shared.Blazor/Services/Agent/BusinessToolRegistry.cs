namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

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
