using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Anthropic Messages API (/v1/messages) implementation of <see cref="IAgentLlmProvider"/>.
/// Normalizes responses and tool calls to the canonical OpenAI-style transcript JSON expected by AgentService.
/// Request payloads (including prompt-caching breakpoints) are built by <see cref="AnthropicAgentRequestBuilder"/>.
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
        var payload = AnthropicAgentRequestBuilder.BuildPayload(model, options.MaxTokens, messages, tools);

        var payloadJson = payload.ToJsonString();
        HttpRequestMessage CreateRequest()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "messages")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", options.ApiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            return request;
        }

        var send = await AgentProviderRetry.SendWithRetryAsync(
            httpClient, CreateRequest, options.MaxProviderAttempts, options.ProviderRetryDelayMs, logger, cancellationToken);
        if (!send.Ok)
            return new LlmCallResult(null, new AgentUsageModel(), send.Error);

        try
        {
            return ParseAnthropicResponse(send.Body!, options.MaxTokens, logger);
        }
        // Malformed gateway payloads vary (missing fields, non-JSON bodies); never let a
        // parse failure escape as HTTP 500 — always return a clean provider error.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Anthropic provider returned an unreadable body.");
            return new LlmCallResult(null, new AgentUsageModel(), "Model server returned an unreadable response.");
        }
    }

    private static LlmCallResult ParseAnthropicResponse(string body, int maxTokens, ILogger logger)
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
            usage.CacheWriteInputTokens = cacheWrite;
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

        // A tool call cut off at max_tokens arrives as broken argument JSON; report the
        // truncation to the loop instead of feeding corrupted tool arguments back to the model.
        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        if (stopReason == "max_tokens" && toolCalls.Count > 0)
        {
            logger.LogWarning("Anthropic tool-call output was truncated at max_tokens={MaxTokens}.", maxTokens);
            return new LlmCallResult(null, usage,
                "Model output was cut off by the token limit while writing a tool call; raise Agent:MaxTokens or narrow the step.");
        }

        return new LlmCallResult(assistant.ToJsonString(), usage, null);
    }
}
