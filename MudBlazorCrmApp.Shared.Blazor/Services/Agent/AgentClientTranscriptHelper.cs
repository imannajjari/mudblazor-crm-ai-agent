using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public readonly record struct AgentToolCall(string Id, string Name, string ArgsJson);

public static partial class AgentClientTranscriptHelper
{
    [GeneratedRegex(@"<attachment\b[^>]*>[\s\S]*?</attachment>", RegexOptions.IgnoreCase)]
    private static partial Regex AttachmentBlockRegex();

    public static List<string> BuildInitialMessages(
        IEnumerable<AgentClientService.AgentChatEntry> priorTurns,
        string goal,
        string snapshotJson)
    {
        var messages = new List<string>();
        foreach (var turn in priorTurns)
        {
            var safeText = AttachmentBlockRegex().Replace(turn.Text ?? string.Empty, "[attachment omitted]");
            messages.Add(JsonSerializer.Serialize(new { role = turn.Kind, content = safeText }));
        }

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

    public static string SerializeFinishBlockedNudge(string reason) =>
        JsonSerializer.Serialize(new
        {
            role = "user",
            content = $"[finish blocked] {reason}"
        });

    public static void BlankAttachmentToolResults(List<string> messages, ILogger? logger = null)
    {
        var attachmentCallIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
        {
            try
            {
                var node = JsonNode.Parse(messages[i]);
                if (node is not JsonObject obj) continue;
                var role = obj["role"]?.GetValue<string>();
                if (role == "assistant" && obj["tool_calls"] is JsonArray tcs)
                {
                    foreach (var tc in tcs)
                    {
                        var fnName = tc?["function"]?["name"]?.GetValue<string>();
                        var tcId = tc?["id"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(tcId) && fnName is "list_attachments" or "read_attachment" or "ask_attachment")
                            attachmentCallIds.Add(tcId);
                    }
                }
                else if (role == "tool")
                {
                    var callId = obj["tool_call_id"]?.GetValue<string>();
                    var content = obj["content"]?.GetValue<string>() ?? string.Empty;
                    if ((!string.IsNullOrEmpty(callId) && attachmentCallIds.Contains(callId)) ||
                        AttachmentBlockRegex().IsMatch(content))
                    {
                        obj["content"] = """{"ok":true,"redacted":"[attachment-content-omitted]"}""";
                        messages[i] = obj.ToJsonString();
                    }
                }
            }
            catch (JsonException ex)
            {
                logger?.LogDebug(ex, "Skipped malformed transcript entry while blanking attachments.");
            }
        }
    }

    public static string SerializeToolResult(string callId, string resultJson) =>
        JsonSerializer.Serialize(new { role = "tool", tool_call_id = callId, content = resultJson });

    public static string AppendLedger(string summary, List<string> ledger, string? heading = null)
    {
        if (ledger.Count == 0) return summary;
        var items = ledger.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.Ordinal).ToList();
        if (items.Count == 0) return summary;
        var title = string.IsNullOrWhiteSpace(heading) ? "Actions performed in application:" : heading;
        if (summary.Contains(title, StringComparison.Ordinal)) return summary;
        var bullets = string.Join("\n", items.Select(i => $"- {i}"));
        return string.IsNullOrWhiteSpace(summary)
            ? $"**{title}**\n{bullets}"
            : $"{summary.TrimEnd()}\n\n---\n**{title}**\n{bullets}";
    }

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

    public static Dictionary<string, string> ParseServerToolResults(List<string>? rawResults, ILogger? logger = null)
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
            catch (JsonException ex)
            {
                logger?.LogDebug(ex, "Skipped malformed server tool result JSON.");
            }
        }
        return map;
    }

    public static void TryLogPriorMessage(string raw, List<AgentClientService.AgentChatEntry> entries, ILogger? logger = null)
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
        catch (JsonException ex)
        {
            logger?.LogDebug(ex, "Skipped malformed prior assistant JSON.");
        }
    }
}
