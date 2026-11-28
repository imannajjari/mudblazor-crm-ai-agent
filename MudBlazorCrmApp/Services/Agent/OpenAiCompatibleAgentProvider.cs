using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MudBlazorCrmApp.Services.Agent;

public interface IAgentLlmProvider
{
    Task<LlmCallResult> CompleteAsync(
        IReadOnlyList<JsonObject> messages,
        JsonElement[]? tools,
        CancellationToken cancellationToken);

    Task<LlmCallResult> CompleteAsync(
        IReadOnlyList<JsonObject> messages,
        JsonElement[]? tools,
        string? modelOverride,
        CancellationToken cancellationToken) =>
        CompleteAsync(messages, tools, cancellationToken);
}

public sealed record LlmCallResult(
    string? AssistantJson,
    AgentUsageModel Usage,
    string? Error);

public sealed class OpenAiCompatibleAgentProvider(
    HttpClient httpClient,
    IOptions<AgentOptions> optionsAccessor,
    ILogger<OpenAiCompatibleAgentProvider> logger) : IAgentLlmProvider
{
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
        {
            return new LlmCallResult(null, new AgentUsageModel(), "Agent API key is not configured on the server.");
        }

        var selectedModel = string.IsNullOrWhiteSpace(modelOverride) ? options.Model : modelOverride.Trim();
        var payload = new JsonObject
        {
            ["model"] = selectedModel,
            ["temperature"] = 0.2,
            ["max_tokens"] = options.MaxTokens,
            ["messages"] = new JsonArray(messages.Select(m => m.DeepClone()).ToArray())
        };

        if (tools is { Length: > 0 })
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                toolArray.Add(JsonNode.Parse(tool.GetRawText()));
            }
            payload["tools"] = toolArray;
            payload["tool_choice"] = "auto";
        }

        var payloadJson = payload.ToJsonString();
        HttpRequestMessage CreateRequest()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            return request;
        }

        var send = await AgentProviderRetry.SendWithRetryAsync(
            httpClient, CreateRequest, options.MaxProviderAttempts, options.ProviderRetryDelayMs, logger, cancellationToken);
        if (!send.Ok)
            return new LlmCallResult(null, new AgentUsageModel(), send.Error);

        try
        {
            using var doc = JsonDocument.Parse(send.Body!);
            var root = doc.RootElement;

            var usage = new AgentUsageModel { Calls = 1 };
            if (root.TryGetProperty("usage", out var usageEl))
            {
                if (usageEl.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pi))
                    usage.InputTokens = pi;
                if (usageEl.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var ci))
                    usage.OutputTokens = ci;
                if (usageEl.TryGetProperty("prompt_tokens_details", out var details) &&
                    details.TryGetProperty("cached_tokens", out var cached) &&
                    cached.TryGetInt32(out var cti))
                {
                    usage.CachedInputTokens = cti;
                }
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return new LlmCallResult(null, usage, "Model server returned no choices.");
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("message", out var messageEl) || messageEl.ValueKind != JsonValueKind.Object)
            {
                return new LlmCallResult(null, usage, "Model server returned an unreadable response.");
            }

            // A tool call cut off at max_tokens arrives as broken argument JSON; report the
            // truncation to the loop instead of feeding corrupted tool arguments back to the model.
            var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
            if (finishReason == "length" &&
                messageEl.TryGetProperty("tool_calls", out var tcs) &&
                tcs.ValueKind == JsonValueKind.Array && tcs.GetArrayLength() > 0)
            {
                logger.LogWarning("Model tool-call output was truncated at max_tokens={MaxTokens}.", options.MaxTokens);
                return new LlmCallResult(null, usage,
                    "Model output was cut off by the token limit while writing a tool call; raise Agent:MaxTokens or narrow the step.");
            }

            return new LlmCallResult(messageEl.GetRawText(), usage, null);
        }
        // Malformed gateway payloads vary (null tool_calls, missing fields, non-JSON bodies);
        // never let a parse failure escape as HTTP 500 — always return a clean provider error.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Model server returned an unreadable body.");
            return new LlmCallResult(null, new AgentUsageModel(), "Model server returned an unreadable response.");
        }
    }
}
