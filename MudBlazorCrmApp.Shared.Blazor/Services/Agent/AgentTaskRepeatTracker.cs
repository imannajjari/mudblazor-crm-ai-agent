using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Per-task repeat detector: blocks an exact (call + screen fingerprint) repeat past its cap,
/// caps the same call and the same tool overall, and counts loop-guard refusals toward the
/// forced "[final call]" finish.
/// </summary>
public sealed class AgentTaskRepeatTracker
{
    public const int MaxSameCall = 3;
    public const int MaxSameCallAnywhere = 15;
    public const int MaxSelectTreeNodeCalls = 12;
    public const int MaxLoopBlocks = 2;
    public const int LearningNudgeMinActions = 6;

    private const string SelectTreeNodeTool = "select_tree_node";

    private readonly Dictionary<string, int> _exactWithFingerprint = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sameCallAnyFingerprint = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _callsPerTool = new(StringComparer.Ordinal);

    private int _actionsExecuted;
    private bool _knowledgeWriteUsed;

    public string? LastFingerprint { get; private set; }
    public int LoopBlocks { get; private set; }
    public bool FiltersCleared { get; set; }
    public bool LearningNudged { get; private set; }

    /// <summary>Counts task-wide tool calls (server and UI) for the one-shot learning nudge.</summary>
    public void MarkAction(string name)
    {
        if (name is "finish" or "ask_user") return;
        _actionsExecuted++;
        if (name is "remember" or "correct_fact") _knowledgeWriteUsed = true;
    }

    /// <summary>True once per task when a hard multi-step task finished without any knowledge write.
    /// A task already forced to finish by the loop guard is not nudged.</summary>
    public bool ShouldNudgeLearning()
    {
        if (LearningNudged || _knowledgeWriteUsed || LoopBlocks >= MaxLoopBlocks ||
            _actionsExecuted < LearningNudgeMinActions)
            return false;
        LearningNudged = true;
        return true;
    }

    public string? PreBlockReason(string name, string argsJson)
    {
        var key = CallKey(name, argsJson);

        if (_sameCallAnyFingerprint.TryGetValue(key, out var anyCount) && anyCount >= MaxSameCallAnywhere)
            return $"[loop guard] '{name}' with identical arguments already ran {MaxSameCallAnywhere} times this task. Stop repeating it; take a different step or finish with a summary.";

        var toolCap = name == SelectTreeNodeTool ? MaxSelectTreeNodeCalls : int.MaxValue;
        if (_callsPerTool.TryGetValue(name, out var toolCount) && toolCount >= toolCap)
            return $"[loop guard] '{name}' reached its per-task limit of {toolCap} calls. Stop calling it; proceed differently or finish with a summary.";

        if (LastFingerprint != null
            && _exactWithFingerprint.TryGetValue(key + "#" + LastFingerprint, out var exactCount)
            && exactCount >= MaxSameCall)
            return $"[loop guard] '{name}' with identical arguments already ran {MaxSameCall} times and the screen did not change in between. The result will not change; try a different approach or finish with a summary.";

        return null;
    }

    public void Record(string name, string argsJson, string? fingerprint)
    {
        _callsPerTool[name] = _callsPerTool.GetValueOrDefault(name) + 1;
        var key = CallKey(name, argsJson);
        _sameCallAnyFingerprint[key] = _sameCallAnyFingerprint.GetValueOrDefault(key) + 1;
        if (string.IsNullOrWhiteSpace(fingerprint)) return;
        LastFingerprint = fingerprint;
        var exactKey = key + "#" + fingerprint;
        _exactWithFingerprint[exactKey] = _exactWithFingerprint.GetValueOrDefault(exactKey) + 1;
    }

    public void NoteFingerprint(string? fingerprint)
    {
        if (!string.IsNullOrWhiteSpace(fingerprint)) LastFingerprint = fingerprint;
    }

    public void MarkLoopBlock() => LoopBlocks++;

    public static string? ExtractFingerprint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var scope in new[] { root, ChildObject(root, "result"), ChildObject(root, "snapshot") })
            {
                if (scope.ValueKind != JsonValueKind.Object) continue;
                if (scope.TryGetProperty("fingerprint", out var fp) && fp.ValueKind == JsonValueKind.String)
                {
                    var value = fp.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        catch (JsonException) { /* non-JSON tool results simply carry no fingerprint */ }
        return null;
    }

    private static JsonElement ChildObject(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(propertyName, out var child) && child.ValueKind == JsonValueKind.Object
            ? child
            : default;

    // Key arguments canonically (sorted keys, no formatting) so {"id":"x"} and {"id": "x" }
    // cannot slip past the caps by re-spacing or reordering JSON.
    private static string CallKey(string name, string argsJson)
    {
        var trimmed = argsJson.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return name + ":" + WriteCanonical(doc.RootElement);
        }
        catch (JsonException)
        {
            return name + ":" + trimmed;
        }
    }

    private static string WriteCanonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name, JsonSerializerOptions.Default) + ":" + WriteCanonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(WriteCanonical)) + "]",
        _ => element.GetRawText()
    };
}
