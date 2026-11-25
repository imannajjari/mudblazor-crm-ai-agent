using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

internal readonly record struct AgentToolCall(string Id, string Name, string ArgsJson);

internal static class AgentClientTranscriptHelper
{
    public static List<string> BuildInitialMessages(
        IEnumerable<AgentClientService.AgentChatEntry> priorTurns,
        string goal,
        string snapshotJson)
    {
        var messages = new List<string>();
        foreach (var turn in priorTurns)
            messages.Add(JsonSerializer.Serialize(new { role = turn.Kind, content = turn.Text }));

        var date = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        messages.Add(JsonSerializer.Serialize(new
        {
            role = "user",
            content = $"Goal: {goal}\nDate: {date}\nCurrent screen snapshot:\n{snapshotJson}"
        }));
        return messages;
    }

    public static string SerializeEmptyReplyRetry() =>
        JsonSerializer.Serialize(new
        {
            role = "user",
            content = "Your previous reply was empty. Please provide a clear answer or summary for the user."
        });

    public static string SerializeToolResult(string callId, string resultJson) =>
        JsonSerializer.Serialize(new { role = "tool", tool_call_id = callId, content = resultJson });

    public static string AppendLedger(string summary, List<string> ledger) =>
        ledger.Count == 0
            ? summary
            : $"{summary}\n\n---\n**Actions executed:** {string.Join(" → ", ledger)}";

    public static List<AgentClientService.AgentChatEntry> DeserializeHistory(
        string? historyJson,
        string chatKey,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(historyJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<AgentClientService.AgentChatEntry>>(historyJson) ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize HistoryJson for {ChatKey}.", chatKey);
            return [];
        }
    }

    public static bool TryParseToolCalls(
        string assistantJson,
        out string? textReply,
        out List<AgentToolCall> calls)
    {
        textReply = null;
        calls = [];
        try
        {
            using var doc = JsonDocument.Parse(assistantJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tool_calls", out var tcs) ||
                tcs.ValueKind != JsonValueKind.Array ||
                tcs.GetArrayLength() == 0)
            {
                textReply = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : string.Empty;
                return true;
            }

            foreach (var tc in tcs.EnumerateArray())
            {
                var id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? Guid.NewGuid().ToString("N")
                    : Guid.NewGuid().ToString("N");
                if (!tc.TryGetProperty("function", out var fn)) return false;
                var name = fn.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                    ? nEl.GetString() ?? string.Empty
                    : string.Empty;
                var args = fn.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String
                    ? aEl.GetString() ?? "{}"
                    : "{}";
                calls.Add(new AgentToolCall(id, name, args));
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static Dictionary<string, string> ParseServerToolResults(List<string>? rawResults)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rawResults == null) return map;
        foreach (var raw in rawResults)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("tool_call_id", out var idEl) &&
                    idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrEmpty(id)) map[id] = raw;
                }
            }
            catch (JsonException) { }
        }
        return map;
    }

    public static void TryLogPriorMessage(string raw, List<AgentClientService.AgentChatEntry> entries)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("role", out var r) || r.ValueKind != JsonValueKind.String || r.GetString() != "assistant") return;
            if (!doc.RootElement.TryGetProperty("tool_calls", out var tcs) || tcs.ValueKind != JsonValueKind.Array) return;

            foreach (var tc in tcs.EnumerateArray())
            {
                if (!tc.TryGetProperty("function", out var fn)) continue;
                var name = fn.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : string.Empty;
                var args = fn.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String ? aEl.GetString() : "{}";
                entries.Add(new AgentClientService.AgentChatEntry("tool", $"[server] {name}({args})"));
            }
        }
        catch (JsonException) { }
    }
}
