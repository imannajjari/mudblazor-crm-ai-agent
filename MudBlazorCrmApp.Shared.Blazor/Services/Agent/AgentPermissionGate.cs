using System.Text.Json;

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
    private readonly HashSet<string> _approvedDestructiveTargets = new(StringComparer.OrdinalIgnoreCase);

    public void ResetSession()
    {
        _alwaysAllowed.Clear();
        _declined.Clear();
        _approvedDestructiveTargets.Clear();
    }

    public void ClearTaskDeclines()
    {
        _declined.Clear();
        _approvedDestructiveTargets.Clear();
    }

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

        if (risk == "destructive" && !ValidateDestructiveTarget(route, inDialog, targetName, snapshotJson))
            return (false, "Destructive action requires an explicit targetName present on screen.");

        var effectiveRisk = risk == "confirm" ? "save" : risk;
        var needsPrompt = risk is "destructive" or "confirm" || effectiveRisk switch
        {
            "read" => false,
            "save" or "write" => mode != AgentMode.Auto && !_alwaysAllowed.Contains(scopeKey),
            _ => mode != AgentMode.Auto && !_alwaysAllowed.Contains(scopeKey) // Fail closed for unknown risk classifications
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

        if (decision == PermissionDecision.AllowAlways && effectiveRisk is "light" or "export")
            _alwaysAllowed.Add(scopeKey);
        if (effectiveRisk == "destructive" && !string.IsNullOrWhiteSpace(targetName))
            _approvedDestructiveTargets.Add($"{route}|{targetName.Trim()}");

        return (true, null);
    }

    private bool ValidateDestructiveTarget(string route, bool inDialog, string? targetName, string snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(targetName) || targetName.Trim().Length < 2)
            return false;

        if (SnapshotContainsTarget(snapshotJson, targetName))
            return true;

        var key = $"{route}|{targetName.Trim()}";
        return inDialog && _approvedDestructiveTargets.Remove(key);
    }

    private static bool SnapshotContainsTarget(string snapshotJson, string targetName)
    {
        var q = targetName.Trim();
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String &&
                (t.GetString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                return true;
            if (root.TryGetProperty("dialogText", out var dt) && dt.ValueKind == JsonValueKind.String &&
                (dt.GetString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                return true;

            if (root.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fields.EnumerateArray())
                {
                    if (f.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String &&
                        (v.GetString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            if (root.TryGetProperty("grids", out var grids) && grids.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in grids.EnumerateArray())
                {
                    if (!g.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
                    foreach (var r in rows.EnumerateArray())
                    {
                        if (!r.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array) continue;
                        if (cells.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && (c.GetString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)))
                            return true;
                    }
                }
            }
        }
        catch (JsonException) { return false; }
        return false;
    }
}
