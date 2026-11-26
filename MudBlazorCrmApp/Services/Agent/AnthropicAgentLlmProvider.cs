using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Anthropic Messages API (/v1/messages) implementation of <see cref="IAgentLlmProvider"/>.
/// Normalizes responses and tool calls to the canonical OpenAI-style transcript JSON expected by AgentService.
/// </summary>
public sealed class AnthropicAgentLlmProvider(
    HttpClient httpClient,
    IOptions<AgentOptions> optionsAccessor,
    ILogger<AnthropicAgentLlmProvider> logger) : IAgentLlmProvider
{
    private const string AnthropicVersion = "2023-06-01";

    public Task<LlmCallResult> CompleteAsync(
        IReadOnlyList<JsonObject> messages,
        JsonElement[]? tools,
        CancellationToken cancellationToken) =>
        CompleteAsync(messages, tools, null, cancellationToken);

    public async Task<LlmCallResult> CompleteAsync(
        IReadOnlyList<JsonObject> messages,
        JsonElement[]? tools,
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        options.Normalize();
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            return new LlmCallResult(null, new AgentUsageModel(), "Agent API key is not configured on the server.");

        var model = string.IsNullOrWhiteSpace(modelOverride) ? options.Model : modelOverride.Trim();
        var (systemText, convertedMessages) = ConvertMessages(messages);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = options.MaxTokens,
            ["temperature"] = 0.2,
            ["messages"] = convertedMessages
        };
        if (!string.IsNullOrWhiteSpace(systemText))
            payload["system"] = systemText;
        if (tools is { Length: > 0 })
            payload["tools"] = ConvertTools(tools);

        using var request = new HttpRequestMessage(HttpMethod.Post, "messages")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", options.ApiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new LlmCallResult(null, new AgentUsageModel(), FormatError(response.StatusCode, body));

            return ParseAnthropicResponse(body);
        }
        catch (TaskCanceledException)
        {
            return new LlmCallResult(null, new AgentUsageModel(), "Model server request timed out.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calling Anthropic LLM provider.");
            return new LlmCallResult(null, new AgentUsageModel(), "Model server is unreachable.");
        }
    }

    private string FormatError(HttpStatusCode code, string body)
    {
        logger.LogWarning("Anthropic provider returned {StatusCode}: {Body}", (int)code, body);
        return code switch
        {
            HttpStatusCode.ServiceUnavailable => "Model server is temporarily busy (503). Please try again in a moment.",
            HttpStatusCode.TooManyRequests => "Model server rate limit reached (429). Please wait a moment.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Model server rejected the API key.",
            _ => $"Model server returned HTTP {(int)code}."
        };
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
            if (role == "assistant" && msg["tool_calls"] is JsonArray tcs)
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
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = "" });
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

    private static LlmCallResult ParseAnthropicResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var usage = new AgentUsageModel { Calls = 1 };
        if (root.TryGetProperty("usage", out var u))
        {
            var baseIn = u.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var iv) ? iv : 0;
            var cacheRead = u.TryGetProperty("cache_read_input_tokens", out var ct) && ct.TryGetInt32(out var cv) ? cv : 0;
            var cacheWrite = u.TryGetProperty("cache_creation_input_tokens", out var cw) && cw.TryGetInt32(out var cwv) ? cwv : 0;
            usage.CachedInputTokens = cacheRead;
            usage.InputTokens = baseIn + cacheRead + cacheWrite;
            if (u.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var ov)) usage.OutputTokens = ov;
        }

        var textParts = new List<string>();
        var toolCalls = new JsonArray();
        if (root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in blocks.EnumerateArray())
            {
                var type = b.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                if (type == "text" && b.TryGetProperty("text", out var txt))
                    textParts.Add(txt.GetString() ?? "");
                else if (type == "tool_use")
                {
                    var id = b.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                    var name = b.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    var inputJson = b.TryGetProperty("input", out var inEl) ? inEl.GetRawText() : "{}";
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = inputJson }
                    });
                }
            }
        }

        var assistant = new JsonObject { ["role"] = "assistant", ["content"] = string.Join("\n", textParts) };
        if (toolCalls.Count > 0) assistant["tool_calls"] = toolCalls;
        return new LlmCallResult(assistant.ToJsonString(), usage, null);
    }
}
