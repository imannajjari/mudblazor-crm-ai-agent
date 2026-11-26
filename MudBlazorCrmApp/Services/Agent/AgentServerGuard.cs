using System.Text.Json;
using System.Text.Json.Nodes;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Tracks per-task server tool state to block duplicate calls, excessive paging walks,
/// repeated identical errors, ask_help overuse, and read_data after delete refusal.
/// </summary>
public sealed class AgentServerGuard
{
    private const int MaxAskHelpCalls = 3;
    private const int MaxPagingWalksPerSource = 2;

    private readonly HashSet<string> _seenCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pagingCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenErrors = new(StringComparer.Ordinal);
    private int _askHelpCount;
    private bool _deleteRefused;

    public static AgentServerGuard FromTranscript(IReadOnlyList<JsonObject> messages, IReadOnlySet<string> serverToolNames)
    {
        var guard = new AgentServerGuard();
        foreach (var msg in messages)
        {
            var role = TryGetString(msg["role"]);
            if (role == "assistant" && msg["tool_calls"] is JsonArray tcs)
            {
                foreach (var tc in tcs)
                {
                    var name = TryGetString(tc?["function"]?["name"]) ?? "";
                    if (!serverToolNames.Contains(name)) continue;
                    var args = TryGetString(tc?["function"]?["arguments"]) ?? "{}";
                    guard.RecordCallSignature(name, args);
                }
            }
            else if (role == "tool")
            {
                var content = TryGetString(msg["content"]) ?? "";
                guard.InspectPriorToolResult(content);
            }
        }
        return guard;
    }

    public static bool IsHowToQuestion(IReadOnlyList<JsonObject> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (TryGetString(messages[i]["role"]) != "user") continue;
            var content = (TryGetString(messages[i]["content"]) ?? "").TrimStart();
            if (content.StartsWith(AgentKnowledgeWriteGuard.NotesMark, StringComparison.Ordinal) ||
                content.StartsWith("Relevant CRM Guide Context", StringComparison.Ordinal) ||
                content.StartsWith("[finish blocked]", StringComparison.OrdinalIgnoreCase) ||
                content.StartsWith("[loop guard]", StringComparison.OrdinalIgnoreCase) ||
                content.StartsWith("[final call]", StringComparison.OrdinalIgnoreCase) ||
                content.StartsWith("Your previous reply was empty", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var goal = ExtractGoalText(content);
            return goal.StartsWith("[how-to question]", StringComparison.OrdinalIgnoreCase)
                || goal.StartsWith("how do i ", StringComparison.OrdinalIgnoreCase)
                || goal.StartsWith("how to ", StringComparison.OrdinalIgnoreCase)
                || goal.StartsWith("how can i ", StringComparison.OrdinalIgnoreCase)
                || goal.StartsWith("چطور ", StringComparison.Ordinal)
                || goal.StartsWith("چگونه ", StringComparison.Ordinal);
        }
        return false;
    }

    // Client transcripts wrap user tasks as "Goal: [att:...] [Delete task: yes]\n<goal>\nDate: ...".
    internal static string ExtractGoalText(string content)
    {
        if (!content.StartsWith("Goal:", StringComparison.OrdinalIgnoreCase))
            return content;

        var afterPrefix = content[5..];
        var dateIdx = afterPrefix.IndexOf("\nDate:", StringComparison.OrdinalIgnoreCase);
        var body = dateIdx >= 0 ? afterPrefix[..dateIdx] : afterPrefix;
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("[att:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Equals("[Delete task: yes]", StringComparison.OrdinalIgnoreCase)) continue;
            return line;
        }
        return string.Empty;
    }

    public static bool ShouldForceFinalCall(
        IReadOnlyList<JsonObject> messages,
        AgentStepRequestModel request,
        AgentOptions options,
        AgentUsageModel totalUsage)
    {
        if (request.ModelCallsSoFar + totalUsage.Calls >= options.MaxTaskModelCalls)
            return true;
        if (request.CostSoFarUsd + (totalUsage.Cost ?? 0m) >= options.MaxTaskCostUsd)
            return true;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (TryGetString(messages[i]["role"]) != "user") continue;
            var content = TryGetString(messages[i]["content"]) ?? "";
            return content.Contains("[final call]", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string? TryGetString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public string? CheckBeforeExecute(string toolName, string argsRaw, JsonElement args)
    {
        var sig = $"{toolName}:{argsRaw.Trim()}";
        if (!_seenCalls.Add(sig))
            return Block("Identical server tool call already executed in this task. Use the previous result.", repeatGuard: true);

        if (toolName == "ask_help")
        {
            _askHelpCount++;
            if (_askHelpCount > MaxAskHelpCalls)
                return Block($"ask_help limit ({MaxAskHelpCalls}) reached for this task. Answer from what you already retrieved.", repeatGuard: true);
        }

        if (toolName == "read_data")
        {
            if (_deleteRefused)
                return Block("read_data is blocked after a delete refusal in this task.", repeatGuard: false);

            var source = args.TryGetProperty("source", out var srcEl) && srcEl.ValueKind == JsonValueKind.String
                ? srcEl.GetString() ?? ""
                : "";
            var skip = args.TryGetProperty("skip", out var skipEl) && skipEl.TryGetInt32(out var s) ? s : 0;
            if (skip > 0 && !string.IsNullOrEmpty(source))
            {
                _pagingCounts.TryGetValue(source, out var pages);
                pages++;
                _pagingCounts[source] = pages;
                if (pages > MaxPagingWalksPerSource)
                    return Block($"Paging walk limit ({MaxPagingWalksPerSource}) reached for '{source}'. Summarize or narrow your search filter.", repeatGuard: true);
            }
        }

        return null;
    }

    public string RecordAndCheckError(string toolName, string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False &&
                root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
            {
                var errKey = $"{toolName}:{errEl.GetString()}";
                if (!_seenErrors.Add(errKey))
                {
                    return Block($"Same server error occurred twice for {toolName}: {errEl.GetString()}. Do not retry this call.", repeatGuard: true);
                }
            }
        }
        catch (JsonException) { /* non-JSON tool result */ }
        return resultJson;
    }

    private void RecordCallSignature(string name, string argsRaw)
    {
        _seenCalls.Add($"{name}:{argsRaw.Trim()}");
        if (name == "ask_help") _askHelpCount++;
        if (name == "read_data")
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsRaw) ? "{}" : argsRaw);
                var root = doc.RootElement;
                var skip = root.TryGetProperty("skip", out var skipEl) && skipEl.TryGetInt32(out var s) ? s : 0;
                var source = root.TryGetProperty("source", out var srcEl) && srcEl.ValueKind == JsonValueKind.String ? srcEl.GetString() ?? "" : "";
                if (skip > 0 && !string.IsNullOrEmpty(source))
                    _pagingCounts[source] = _pagingCounts.GetValueOrDefault(source) + 1;
            }
            catch (JsonException) { /* ignore malformed historical args */ }
        }
    }

    private void InspectPriorToolResult(string content)
    {
        if (content.Contains("\"deleteRefused\":true", StringComparison.OrdinalIgnoreCase))
        {
            _deleteRefused = true;
        }
    }

    private static string Block(string reason, bool repeatGuard) =>
        JsonSerializer.Serialize(new { ok = false, blocked = true, repeatGuard, reason });
}
