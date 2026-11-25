namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public enum AgentMode
{
    Manual,
    SemiAuto,
    Auto
}

public enum PermissionDecision
{
    AllowOnce,
    AllowAlways,
    Deny
}

public sealed class AgentPermissionGate
{
    private readonly HashSet<string> _alwaysAllowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _declined = new(StringComparer.OrdinalIgnoreCase);

    public void ResetSession()
    {
        _alwaysAllowed.Clear();
        _declined.Clear();
    }

    public void ClearTaskDeclines() => _declined.Clear();

    public async Task<(bool Allowed, string? BlockReason)> AuthorizeAsync(
        AgentMode mode,
        string route,
        bool inDialog,
        string actionText,
        string risk,
        string? targetName,
        string snapshotJson,
        Func<string, string, CancellationToken, Task<PermissionDecision>> promptAsync,
        CancellationToken ct)
    {
        var scopeKey = $"{route}|{inDialog}|{actionText}";
        if (_declined.Contains(scopeKey))
            return (false, $"User previously declined '{actionText}'. Do not retry.");

        // Destructive actions ALWAYS require confirmation unless Auto mode AND targetName is verified on screen
        if (risk == "destructive" && (string.IsNullOrWhiteSpace(targetName) || !snapshotJson.Contains(targetName.Trim(), StringComparison.OrdinalIgnoreCase)))
            return (false, "Destructive action requires an explicit targetName present on screen.");

        var effectiveRisk = risk == "confirm" && inDialog ? "save" : risk;
        var needsPrompt = effectiveRisk switch
        {
            "destructive" => true, // Never auto-approve destructive actions without explicit user confirmation
            "save" => mode != AgentMode.Auto && !_alwaysAllowed.Contains(scopeKey),
            _ => false
        };

        if (!needsPrompt)
            return (true, null);

        var label = string.IsNullOrWhiteSpace(targetName) ? actionText : $"{actionText}: {targetName}";
        var decision = await promptAsync(label, effectiveRisk, ct);
        if (decision == PermissionDecision.Deny)
        {
            _declined.Add(scopeKey);
            return (false, $"User declined '{label}'. Do not retry.");
        }

        if (decision == PermissionDecision.AllowAlways && effectiveRisk != "destructive")
            _alwaysAllowed.Add(scopeKey);

        return (true, null);
    }
}
