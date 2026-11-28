using System.Text.Json;
using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Tracks unsaved form edits, delete lifecycle, record binding, file guards, and safety checks.
/// </summary>
public sealed partial class AgentSafetyState
{
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "TBD", "TODO", "N/A", "NA", "UNKNOWN", "PLACEHOLDER", "DUMMY", "XXX"
    };

    [GeneratedRegex(@"^([a-zA-Z]:\\|/tmp/|/var/|data:[^;]+;base64,)", RegexOptions.IgnoreCase)]
    private static partial Regex FileValuePattern();

    // Kept aligned with the JS snapshot masking regex in agentDriver.js so the ledger never
    // logs a value the snapshot showed masked.
    [GeneratedRegex(@"((?<![a-z])(password|token|secret|code|key|auth)(?![a-z])|رمز|عبور|کارت|شناسه)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveLabelPattern();

    public bool HasUnsavedChanges { get; private set; }
    public bool DeleteTaskRequested { get; set; }
    public bool HasReadBeforeDelete { get; private set; }
    public bool DeleteRefused { get; private set; }
    public bool HasAttachmentsInTask { get; set; }
    public bool HasAskedFileSaveConfirm { get; private set; }
    public bool AutoAdviceNudged { get; private set; }
    public string? SelectedRecordHint { get; set; }
    private string _userEvidence = string.Empty;

    public void MarkDirty() => HasUnsavedChanges = true;
    public void MarkSaved() => HasUnsavedChanges = false;
    public void MarkRead() => HasReadBeforeDelete = true;
    public void MarkDeleteRefused() => DeleteRefused = true;
    public void MarkFileSaveConfirmed() => HasAskedFileSaveConfirm = true;
    public void MarkAutoAdviceNudged() => AutoAdviceNudged = true;

    public void SetUserEvidence(string? evidence) => _userEvidence = CollapseWhitespace(evidence ?? string.Empty);
    public void AppendUserEvidence(string? text) =>
        _userEvidence = CollapseWhitespace(_userEvidence + "\n" + (text ?? string.Empty));

    /// <summary>True when the text appears as a whole fragment in the goal or the user's ask_user answers of this task.</summary>
    public bool IsUserQuote(string? text)
    {
        var quote = CollapseWhitespace(text ?? string.Empty);
        if (quote.Length < 3) return false;
        var idx = _userEvidence.IndexOf(quote, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var cleanBefore = idx == 0 || !char.IsLetterOrDigit(_userEvidence[idx - 1]);
            var end = idx + quote.Length;
            var cleanAfter = end >= _userEvidence.Length || !char.IsLetterOrDigit(_userEvidence[end]);
            if (cleanBefore && cleanAfter) return true;
            idx = _userEvidence.IndexOf(quote, idx + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string CollapseWhitespace(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public void Reset()
    {
        HasUnsavedChanges = false;
        DeleteTaskRequested = false;
        HasReadBeforeDelete = false;
        DeleteRefused = false;
        HasAttachmentsInTask = false;
        HasAskedFileSaveConfirm = false;
        AutoAdviceNudged = false;
        SelectedRecordHint = null;
        _userEvidence = string.Empty;
    }

    public static bool IsProvidedValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return !PlaceholderValues.Contains(value.Trim());
    }

    public static bool IsFileLikeFieldValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (FileValuePattern().IsMatch(value.Trim()) || value.Length > 2000 && !value.Contains(' '));

    public string? CheckReplacedDelete(string risk) =>
        DeleteRefused && risk is "save" or "write"
            ? "Cannot perform write or replacement actions after a delete action was refused in this task."
            : null;

    public string? CheckFileSaveConfirmationNeeded(string risk) =>
        HasAttachmentsInTask && HasUnsavedChanges && risk == "save" && !HasAskedFileSaveConfirm
            ? "You filled data during a task with attached files. Ask the user via ask_user to confirm before saving."
            : null;

    public string? CheckDeleteReadPrerequisite() =>
        DeleteTaskRequested && !HasReadBeforeDelete
            ? "Inspect the target record on screen or via read_data/select_grid_row before executing a destructive delete."
            : null;

    public string? CheckWrongRecord(string snapshotJson, string? targetName)
    {
        var explicitTarget = !string.IsNullOrWhiteSpace(targetName) ? targetName.Trim() : null;
        var hint = !string.IsNullOrWhiteSpace(SelectedRecordHint) ? SelectedRecordHint.Trim() : null;
        if ((explicitTarget is null || explicitTarget.Length < 3) && (hint is null || hint.Length < 3))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("inDialog", out var d) || d.ValueKind != JsonValueKind.True)
                return null;

            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var dialogText = root.TryGetProperty("dialogText", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString() ?? "" : "";
            var fieldValues = root.TryGetProperty("fields", out var fArr) && fArr.ValueKind == JsonValueKind.Array
                ? string.Join("\n", fArr.EnumerateArray()
                    .Select(f => f.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                    .Where(v => !string.IsNullOrWhiteSpace(v)))
                : "";
            var combined = $"{title}\n{dialogText}\n{fieldValues}";

            if (explicitTarget is { Length: >= 3 } && !combined.Contains(explicitTarget, StringComparison.OrdinalIgnoreCase))
                return $"Open dialog content does not mention expected target record '{explicitTarget}'. Verify the selected row before acting.";

            if (hint is { Length: >= 3 } && !combined.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return $"Open dialog content does not match previously selected record '{hint}'. Verify the selected row before acting.";
        }
        catch (JsonException) { /* invalid snapshot handled elsewhere */ }
        return null;
    }

    public static bool IsSensitiveField(string snapshotJson, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            if (!doc.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var f in fields.EnumerateArray())
            {
                if (f.TryGetProperty("id", out var idEl) && idEl.GetString() == id)
                {
                    var kind = f.TryGetProperty("kind", out var kEl) ? kEl.GetString() ?? "" : "";
                    var label = f.TryGetProperty("label", out var lEl) ? lEl.GetString() ?? "" : "";
                    return string.Equals(kind, "password", StringComparison.OrdinalIgnoreCase) || SensitiveLabelPattern().IsMatch(label);
                }
            }
        }
        catch (JsonException) { /* ignore malformed snapshot */ }
        return false;
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
