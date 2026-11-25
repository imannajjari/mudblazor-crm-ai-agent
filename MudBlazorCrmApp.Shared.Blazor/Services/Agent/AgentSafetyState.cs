using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Tracks unsaved form edits and enforces Save, Finish, Value, and Delete safety guards.
/// </summary>
public sealed class AgentSafetyState
{
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "TBD", "TODO", "N/A", "NA", "UNKNOWN", "PLACEHOLDER", "DUMMY", "XXX"
    };

    public bool HasUnsavedChanges { get; private set; }

    public void MarkDirty() => HasUnsavedChanges = true;
    public void MarkSaved() => HasUnsavedChanges = false;
    public void Reset() => HasUnsavedChanges = false;

    public static bool IsProvidedValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return !PlaceholderValues.Contains(value.Trim());
    }

    public string? CheckFinishOrNavAllowed() =>
        HasUnsavedChanges
            ? "Unsaved changes exist on the current form. Save or cancel them before finishing or navigating away."
            : null;

    public static string? FindEmptyRequiredField(string snapshotJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            if (!doc.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var f in fields.EnumerateArray())
            {
                var disabled = f.TryGetProperty("disabled", out var disEl) && disEl.ValueKind == JsonValueKind.True;
                if (disabled) continue;
                var required = f.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.True;
                if (!required) continue;

                var val = f.TryGetProperty("value", out var valEl) && valEl.ValueKind == JsonValueKind.String
                    ? valEl.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(val)) continue;

                return f.TryGetProperty("label", out var lblEl) && lblEl.ValueKind == JsonValueKind.String
                    ? lblEl.GetString() ?? "required field"
                    : "required field";
            }
        }
        catch (JsonException) { /* invalid snapshot handled elsewhere */ }
        return null;
    }

    public static string? CheckDeleteBlockers(string snapshotJson, string targetName)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("blocksDelete", out var bd) && bd.ValueKind == JsonValueKind.True)
            {
                var summary = root.TryGetProperty("inUseSummary", out var sum) && sum.ValueKind == JsonValueKind.String
                    ? sum.GetString()
                    : null;
                return string.IsNullOrWhiteSpace(summary)
                    ? $"Cannot delete '{targetName}' because it is currently in use."
                    : $"Cannot delete '{targetName}': {summary}";
            }
        }
        catch (JsonException) { /* ignore */ }
        return null;
    }
}
