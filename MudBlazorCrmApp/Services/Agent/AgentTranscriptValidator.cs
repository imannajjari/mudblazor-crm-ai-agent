using System.Text.Json;
using System.Text.Json.Nodes;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Validates incoming conversation transcripts and enforces tool_call_id pairing.
/// </summary>
public static class AgentTranscriptValidator
{
    public const int MaxTotalMessageChars = 4_000_000;

    public static bool TryParseMessages(
        IReadOnlyList<string>? rawMessages,
        int maxMessages,
        out List<JsonObject> parsed)
    {
        parsed = [];
        if (rawMessages == null || rawMessages.Count == 0 || rawMessages.Count > maxMessages)
            return false;

        long totalChars = 0;
        var knownToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawMessages)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            totalChars += raw.Length;
            if (totalChars > MaxTotalMessageChars) return false;
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
