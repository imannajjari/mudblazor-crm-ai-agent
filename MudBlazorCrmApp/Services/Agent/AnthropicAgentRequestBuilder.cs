using System.Text.Json;
using System.Text.Json.Nodes;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Builds Anthropic /v1/messages payloads from the canonical OpenAI-style transcript:
/// system/role conversion, tool schemas, and three prompt-caching breakpoints
/// (system block, last tool, newest cacheable message block).
/// </summary>
public static class AnthropicAgentRequestBuilder
{
    private const double RequestTemperature = 0.2;

    public static JsonObject BuildPayload(string model, int maxTokens, IReadOnlyList<JsonObject> messages, JsonElement[]? tools)
    {
        var (systemText, convertedMessages) = ConvertMessages(messages);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = RequestTemperature,
            ["messages"] = convertedMessages
        };
        if (!string.IsNullOrWhiteSpace(systemText))
            payload["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = systemText,
                ["cache_control"] = EphemeralBreakpoint()
            });
        if (tools is { Length: > 0 })
        {
            var convertedTools = ConvertTools(tools);
            if (convertedTools.Count > 0)
                convertedTools[^1]!["cache_control"] = EphemeralBreakpoint();
            payload["tools"] = convertedTools;
        }
        MarkNewestMessageBlock(convertedMessages);
        return payload;
    }

    private static JsonObject EphemeralBreakpoint() => new() { ["type"] = "ephemeral" };

    // tool_use blocks cannot carry cache_control, so mark the newest cacheable block;
    // a breakpoint still caches the whole transcript prefix before it.
    private static void MarkNewestMessageBlock(JsonArray messages)
    {
        if (messages.Count == 0 || messages[^1] is not JsonObject last) return;
        if (last["content"] is not JsonArray blocks)
        {
            var text = TryGetString(last["content"]);
            blocks = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = string.IsNullOrEmpty(text) ? " " : text });
            last["content"] = blocks;
        }
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i] is JsonObject block && TryGetString(block["type"]) is "text" or "image" or "document" or "tool_result")
            {
                block["cache_control"] = EphemeralBreakpoint();
                return;
            }
        }
    }

    private static (string SystemText, JsonArray Messages) ConvertMessages(IReadOnlyList<JsonObject> messages)
    {
        var sysParts = new List<string>();
        var list = new JsonArray();
        foreach (var msg in messages)
        {
            var role = TryGetString(msg["role"]) ?? "user";
            if (role == "system")
            {
                var txt = TryGetString(msg["content"]);
                if (!string.IsNullOrWhiteSpace(txt)) sysParts.Add(txt);
                continue;
            }
            if (role == "tool")
            {
                AppendUserTurn(list, new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = TryGetString(msg["tool_call_id"]) ?? "",
                    ["content"] = TryGetString(msg["content"]) ?? ""
                });
                continue;
            }
            if (role == "assistant" && msg["tool_calls"] is JsonArray tcs && tcs.Count > 0)
            {
                list.Add(new JsonObject { ["role"] = "assistant", ["content"] = ConvertAssistantToolCalls(msg, tcs) });
                continue;
            }
            if (role == "assistant")
                list.Add(new JsonObject { ["role"] = "assistant", ["content"] = ConvertUserContent(msg["content"]) });
            else
                AppendUserTurn(list, ConvertUserContent(msg["content"]));
        }
        return (string.Join("\n\n", sysParts), list);
    }

    private static JsonArray ConvertAssistantToolCalls(JsonObject msg, JsonArray tcs)
    {
        var blocks = new JsonArray();
        var text = TryGetString(msg["content"]);
        if (!string.IsNullOrWhiteSpace(text))
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var tc in tcs)
        {
            var fn = TryGetString(tc?["function"]?["name"]) ?? "";
            var argsStr = TryGetString(tc?["function"]?["arguments"]) ?? "{}";
            JsonNode? parsedInput;
            try { parsedInput = JsonNode.Parse(argsStr); }
            catch (JsonException) { parsedInput = new JsonObject(); }
            blocks.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = TryGetString(tc?["id"]) ?? Guid.NewGuid().ToString("N"),
                ["name"] = fn,
                ["input"] = parsedInput ?? new JsonObject()
            });
        }
        return blocks;
    }

    private static void AppendUserTurn(JsonArray list, JsonNode contentNode)
    {
        var blocks = contentNode is JsonArray arr
            ? arr
            : contentNode is JsonObject obj && obj["type"] != null
                ? new JsonArray(obj.DeepClone())
                : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = TryGetString(contentNode) ?? "" });

        if (list.Count > 0 && list[^1] is JsonObject prev && TryGetString(prev["role"]) == "user")
        {
            if (prev["content"] is not JsonArray prevArr)
            {
                var prevText = TryGetString(prev["content"]) ?? "";
                prevArr = string.IsNullOrEmpty(prevText)
                    ? []
                    : [new JsonObject { ["type"] = "text", ["text"] = prevText }];
                prev["content"] = prevArr;
            }
            foreach (var b in blocks)
                if (b != null) prevArr.Add(b.DeepClone());
            return;
        }
        list.Add(new JsonObject { ["role"] = "user", ["content"] = blocks.DeepClone() });
    }

    private static JsonNode ConvertUserContent(JsonNode? content)
    {
        if (content is not JsonArray arr) return content?.DeepClone() ?? JsonValue.Create("")!;
        var blocks = new JsonArray();
        foreach (var item in arr)
        {
            if (TryGetString(item?["type"]) == "image_url")
            {
                var url = TryGetString(item?["image_url"]?["url"]) ?? "";
                var commaIdx = url.IndexOf(',');
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIdx > 5)
                {
                    var header = url[5..commaIdx];
                    var semiIdx = header.IndexOf(';');
                    var mediaType = semiIdx > 0 ? header[..semiIdx] : "image/png";
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = url[(commaIdx + 1)..] }
                    });
                }
            }
            else if (item != null)
            {
                blocks.Add(item.DeepClone());
            }
        }
        if (blocks.Count == 0)
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = " " }); // Anthropic rejects empty text blocks
        return blocks;
    }

    private static string? TryGetString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonArray ConvertTools(JsonElement[] tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
        {
            if (!t.TryGetProperty("function", out var fn)) continue;
            arr.Add(new JsonObject
            {
                ["name"] = fn.GetProperty("name").GetString(),
                ["description"] = fn.TryGetProperty("description", out var d) ? d.GetString() : "",
                ["input_schema"] = fn.TryGetProperty("parameters", out var p) ? JsonNode.Parse(p.GetRawText()) : new JsonObject()
            });
        }
        return arr;
    }
}
