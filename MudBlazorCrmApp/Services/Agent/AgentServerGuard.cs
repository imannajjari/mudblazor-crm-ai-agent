using System.Text.Json;
using System.Text.Json.Nodes;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Validates incoming conversation transcripts and enforces tool_call_id pairing.
/// </summary>
public static class AgentTranscriptValidator
{
    public static bool TryParseMessages(
        IReadOnlyList<string>? rawMessages,
        int maxMessages,
        out List<JsonObject> parsed)
    {
        parsed = [];
        if (rawMessages == null || rawMessages.Count == 0 || rawMessages.Count > maxMessages)
            return false;

        var knownToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawMessages)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            JsonNode? node;
            try { node = JsonNode.Parse(raw); }
            catch (JsonException) { return false; }

            if (node is not JsonObject obj) return false;
            var role = obj["role"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : null;
            if (role is not ("user" or "assistant" or "tool")) return false;

            if (role == "assistant" && obj["tool_calls"] is JsonArray tcs)
            {
                foreach (var tc in tcs)
                {
                    var tcId = tc?["id"] is JsonValue tid && tid.TryGetValue<string>(out var tids) ? tids : null;
                    if (!string.IsNullOrWhiteSpace(tcId)) knownToolCallIds.Add(tcId);
                }
            }
            else if (role == "tool")
            {
                var idStr = obj["tool_call_id"] is JsonValue iv && iv.TryGetValue<string>(out var ids) ? ids : null;
                if (string.IsNullOrWhiteSpace(idStr) || !knownToolCallIds.Contains(idStr))
                    return false;
            }

            parsed.Add(obj);
        }

        return true;
    }
}

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
            var content = TryGetString(messages[i]["content"]) ?? "";
            return content.TrimStart().StartsWith("[how-to question]", StringComparison.OrdinalIgnoreCase);
        }
        return false;
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
        if (content.Contains("\"deleteRefused\":true", StringComparison.OrdinalIgnoreCase) ||
            (content.Contains("\"blocked\":true", StringComparison.OrdinalIgnoreCase) &&
             content.Contains("delete", StringComparison.OrdinalIgnoreCase)))
        {
            _deleteRefused = true;
        }
    }

    private static string Block(string reason, bool repeatGuard) =>
        JsonSerializer.Serialize(new { ok = false, blocked = true, repeatGuard, reason });
}
