using System.Net;
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
    public async Task<LlmCallResult> CompleteAsync(
        IReadOnlyList<JsonObject> messages,
        JsonElement[]? tools,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        options.Normalize();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return new LlmCallResult(null, new AgentUsageModel(), "Agent API key is not configured on the server.");
        }

        var payload = new JsonObject
        {
            ["model"] = options.Model,
            ["temperature"] = 0.2,
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

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("LLM provider returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                var message = response.StatusCode switch
                {
                    HttpStatusCode.ServiceUnavailable => "Model server is temporarily busy (503). Please try again in a moment.",
                    HttpStatusCode.TooManyRequests => "Model server rate limit reached (429). Please wait a moment.",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Model server rejected the API key.",
                    _ => $"Model server returned HTTP {(int)response.StatusCode}."
                };
                return new LlmCallResult(null, new AgentUsageModel(), message);
            }

            using var doc = JsonDocument.Parse(body);
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

            var messageEl = choices[0].GetProperty("message");
            return new LlmCallResult(messageEl.GetRawText(), usage, null);
        }
        catch (TaskCanceledException)
        {
            return new LlmCallResult(null, new AgentUsageModel(), "Model server request timed out.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calling OpenAI-compatible LLM provider.");
            return new LlmCallResult(null, new AgentUsageModel(), "Model server is unreachable.");
        }
    }
}
