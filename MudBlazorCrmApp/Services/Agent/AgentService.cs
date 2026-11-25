using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MudBlazorCrmApp.Services.Agent;

public sealed class AgentService(
    IAgentLlmProvider provider,
    IAgentRateLimiter rateLimiter,
    IEnumerable<IServerToolHandler> serverTools,
    IOptions<AgentOptions> optionsAccessor,
    ILogger<AgentService> logger)
{
    private const int MaxTranscriptMessages = 200;
    private const int MaxServerHops = 4;
    private const int MaxLookTextChars = 30_000;
    private const int MaxLookQuestionChars = 2_000;

    private readonly Dictionary<string, IServerToolHandler> _serverToolMap =
        serverTools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public async Task<AgentStepResponseModel> StepAsync(
        AgentStepRequestModel request,
        string userKey,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        options.Normalize();

        if (!rateLimiter.TryAcquire(userKey, options.MaxCallsPerUserPerMinute))
            return new AgentStepResponseModel { Error = "Too many agent requests in a minute. Wait a moment and try again." };

        if (!TryParseMessages(request.Messages, out var parsedMessages))
            return new AgentStepResponseModel { Error = "The conversation sent to the agent is not valid." };

        var working = new List<JsonObject>(parsedMessages.Count + 1)
        {
            new() { ["role"] = "system", ["content"] = AgentPrompts.MainSystemPrompt }
        };
        working.AddRange(parsedMessages);

        var prior = new List<string>();
        var timings = new List<string>();
        var totalUsage = new AgentUsageModel();
        var seenServerCalls = CollectPriorServerCalls(parsedMessages);

        for (var hop = 0; hop <= MaxServerHops; hop++)
        {
            var sw = Stopwatch.StartNew();
            var llmResult = await provider.CompleteAsync(working, AgentToolSchemas.GetTools(), cancellationToken);
            sw.Stop();
            timings.Add($"llm {sw.ElapsedMilliseconds} ms");
            AgentCostCalculator.Compute(llmResult.Usage, options);
            AgentCostCalculator.Accumulate(totalUsage, llmResult.Usage);

            if (llmResult.Error != null)
                return BuildError(prior, timings, totalUsage, options, llmResult.Error);

            if (string.IsNullOrWhiteSpace(llmResult.AssistantJson))
                return BuildError(prior, timings, totalUsage, options, "Assistant returned an empty response.");

            JsonObject? assistantObj;
            try
            {
                assistantObj = JsonNode.Parse(llmResult.AssistantJson)?.AsObject();
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid JSON returned by LLM provider.");
                return BuildError(prior, timings, totalUsage, options, "Assistant returned invalid JSON.");
            }
            if (assistantObj == null)
                return BuildError(prior, timings, totalUsage, options, "Assistant returned non-object JSON.");

            var toolCalls = assistantObj["tool_calls"]?.AsArray();
            if (toolCalls == null || toolCalls.Count == 0)
                return BuildSuccess(llmResult.AssistantJson, prior, [], timings, totalUsage, options);

            var hasServerTool = toolCalls.Any(tc => _serverToolMap.ContainsKey(tc?["function"]?["name"]?.GetValue<string>() ?? ""));
            var hasUiTool = toolCalls.Any(tc => !_serverToolMap.ContainsKey(tc?["function"]?["name"]?.GetValue<string>() ?? ""));
            if (!hasServerTool)
                return BuildSuccess(llmResult.AssistantJson, prior, [], timings, totalUsage, options);

            var serverResults = await RunServerToolBatchAsync(toolCalls, seenServerCalls, timings, cancellationToken);
            if (hasUiTool || hop == MaxServerHops)
                return BuildSuccess(llmResult.AssistantJson, prior, serverResults, timings, totalUsage, options);

            working.Add(assistantObj);
            prior.Add(llmResult.AssistantJson);
            foreach (var resJson in serverResults)
            {
                working.Add(JsonNode.Parse(resJson)!.AsObject());
                prior.Add(resJson);
            }
        }

        return BuildError(prior, timings, totalUsage, options, "Server tool hop limit reached.");
    }

    private async Task<List<string>> RunServerToolBatchAsync(
        JsonArray toolCalls,
        HashSet<string> seenCalls,
        List<string> timings,
        CancellationToken ct)
    {
        var results = new List<string>();
        foreach (var tc in toolCalls)
        {
            var fnName = tc?["function"]?["name"]?.GetValue<string>() ?? "";
            if (!_serverToolMap.TryGetValue(fnName, out var handler)) continue;

            var callId = tc?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
            var argsRaw = tc?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
            var sw = Stopwatch.StartNew();
            var resultJson = await ExecuteServerToolAsync(handler, argsRaw, seenCalls, ct);
            sw.Stop();
            timings.Add($"{fnName} {sw.ElapsedMilliseconds} ms");

            results.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = callId,
                ["content"] = resultJson
            }.ToJsonString());
        }
        return results;
    }

    private static async Task<string> ExecuteServerToolAsync(
        IServerToolHandler handler,
        string argsRaw,
        HashSet<string> seenCalls,
        CancellationToken ct)
    {
        var signature = $"{handler.Name}:{argsRaw.Trim()}";
        if (!seenCalls.Add(signature))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                blocked = true,
                repeatGuard = true,
                reason = "Identical server tool call already executed in this task. Use the previous result."
            });
        }

        JsonDocument argsDoc;
        try
        {
            argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsRaw) ? "{}" : argsRaw);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { ok = false, error = "Invalid JSON tool arguments." });
        }
        using var _ = argsDoc;
        return await handler.ExecuteAsync(argsDoc.RootElement, ct);
    }

    public async Task<AgentLookResponseModel> LookAsync(
        AgentLookRequestModel request,
        string userKey,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        options.Normalize();

        if (!rateLimiter.TryAcquire(userKey, options.MaxCallsPerUserPerMinute))
            return new AgentLookResponseModel { Error = "Too many agent requests in a minute. Wait a moment and try again." };

        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > MaxLookQuestionChars)
            return new AgentLookResponseModel { Error = "The question is empty or too long." };

        if (string.IsNullOrWhiteSpace(request.Text))
            return new AgentLookResponseModel { Error = "Visible screen text is required." };

        var screenText = request.Text.Length > MaxLookTextChars ? request.Text[..MaxLookTextChars] : request.Text;
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "system", ["content"] = AgentPrompts.LookSystemPrompt },
            new() { ["role"] = "user", ["content"] = $"Question: {request.Question}\n\nVisible screen text:\n{screenText}" }
        };

        var result = await provider.CompleteAsync(messages, tools: null, cancellationToken);
        AgentCostCalculator.Compute(result.Usage, options);
        if (result.Error != null)
            return new AgentLookResponseModel { Error = result.Error, Usage = result.Usage };

        string? answerText = null;
        if (!string.IsNullOrWhiteSpace(result.AssistantJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(result.AssistantJson);
                if (doc.RootElement.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    answerText = contentEl.GetString();
            }
            catch (JsonException)
            {
                return new AgentLookResponseModel { Error = "Model returned invalid JSON.", Usage = result.Usage };
            }
        }

        return new AgentLookResponseModel { Text = answerText ?? string.Empty, Usage = result.Usage };
    }

    public static bool TryParseMessages(IReadOnlyList<string>? rawMessages, out List<JsonObject> parsed)
    {
        parsed = [];
        if (rawMessages == null || rawMessages.Count == 0 || rawMessages.Count > MaxTranscriptMessages)
            return false;

        foreach (var raw in rawMessages)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            JsonNode? node;
            try { node = JsonNode.Parse(raw); }
            catch (JsonException) { return false; }

            if (node is not JsonObject obj) return false;
            var roleNode = obj["role"];
            var role = roleNode is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : null;
            if (role is not ("user" or "assistant" or "tool")) return false;
            if (role == "tool")
            {
                var idNode = obj["tool_call_id"];
                var idStr = idNode is JsonValue iv && iv.TryGetValue<string>(out var ids) ? ids : null;
                if (string.IsNullOrWhiteSpace(idStr)) return false;
            }
            parsed.Add(obj);
        }
        return true;
    }

    private HashSet<string> CollectPriorServerCalls(IReadOnlyList<JsonObject> messages)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            if (msg["role"]?.GetValue<string>() != "assistant") continue;
            var tcs = msg["tool_calls"]?.AsArray();
            if (tcs == null) continue;
            foreach (var tc in tcs)
            {
                var name = tc?["function"]?["name"]?.GetValue<string>() ?? "";
                if (!_serverToolMap.ContainsKey(name)) continue;
                var args = tc?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                set.Add($"{name}:{args.Trim()}");
            }
        }
        return set;
    }

    private static AgentStepResponseModel BuildSuccess(string assistant, List<string> prior, List<string> serverResults, List<string> timings, AgentUsageModel usage, AgentOptions options) =>
        new() { Assistant = assistant, Prior = prior, ServerToolResults = serverResults, Timings = timings, Usage = usage, MaxTaskCostUsd = options.MaxTaskCostUsd, MaxTaskModelCalls = options.MaxTaskModelCalls };

    private static AgentStepResponseModel BuildError(List<string> prior, List<string> timings, AgentUsageModel usage, AgentOptions options, string error) =>
        new() { Prior = prior, Timings = timings, Usage = usage, Error = error, MaxTaskCostUsd = options.MaxTaskCostUsd, MaxTaskModelCalls = options.MaxTaskModelCalls };
}
