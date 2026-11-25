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
    ILogger<AgentService> logger,
    AgentHelpLlmAdvisor? helpAdvisor = null)
{
    private const int MaxTranscriptMessages = 200;
    private const int MaxServerHops = 4;
    private const int MaxLookTextChars = 30_000;
    private const int MaxLookQuestionChars = 2_000;

    private readonly Dictionary<string, IServerToolHandler> _serverToolMap =
        serverTools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    private readonly HashSet<string> _serverToolNames =
        serverTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

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

        var totalUsage = new AgentUsageModel();
        var isHowTo = AgentServerGuard.IsHowToQuestion(parsedMessages);
        var systemPrompt = await BuildSystemPromptWithAutoHelpAsync(parsedMessages, isHowTo, totalUsage, options, cancellationToken);
        var working = new List<JsonObject>(parsedMessages.Count + 1)
        {
            new() { ["role"] = "system", ["content"] = systemPrompt }
        };
        working.AddRange(parsedMessages);

        var prior = new List<string>();
        var timings = new List<string>();
        var guard = AgentServerGuard.FromTranscript(parsedMessages, _serverToolNames);

        for (var hop = 0; hop <= MaxServerHops; hop++)
        {
            var hopResult = await RunSingleHopAsync(working, request, options, totalUsage, isHowTo, hop, guard, prior, timings, cancellationToken);
            if (hopResult != null) return hopResult;
        }

        return BuildError(prior, timings, totalUsage, options, "Server tool hop limit reached.");
    }

    private async Task<string> BuildSystemPromptWithAutoHelpAsync(
        IReadOnlyList<JsonObject> parsedMessages,
        bool isHowTo,
        AgentUsageModel totalUsage,
        AgentOptions options,
        CancellationToken ct)
    {
        if (!isHowTo || parsedMessages.Count != 1 || helpAdvisor == null)
            return AgentPrompts.MainSystemPrompt;

        var firstUserMsg = TryGetString(parsedMessages[0]["content"]) ?? "";
        var (autoHelp, sideUsage) = await helpAdvisor.BuildAutoHelpContextAsync(firstUserMsg, ct);
        AgentCostCalculator.Compute(sideUsage, options);
        AgentCostCalculator.Accumulate(totalUsage, sideUsage);
        return string.IsNullOrWhiteSpace(autoHelp)
            ? AgentPrompts.MainSystemPrompt
            : $"{AgentPrompts.MainSystemPrompt}\n\nRelevant CRM Guide Context (auto-retrieved):\n{autoHelp}";
    }

    private async Task<AgentStepResponseModel?> RunSingleHopAsync(
        List<JsonObject> working,
        AgentStepRequestModel request,
        AgentOptions options,
        AgentUsageModel totalUsage,
        bool isHowTo,
        int hop,
        AgentServerGuard guard,
        List<string> prior,
        List<string> timings,
        CancellationToken cancellationToken)
    {
        var isFinalCall = AgentServerGuard.ShouldForceFinalCall(working, request, options, totalUsage);
        if (isFinalCall)
        {
            working.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "[final call] Step/budget limit reached. Summarize what you have found or completed so far in plain text. Do not call any tools."
            });
        }

        var tools = isFinalCall ? null : (isHowTo ? AgentToolSchemas.GetHowToTools() : AgentToolSchemas.GetTools());
        var sw = Stopwatch.StartNew();
        var llmResult = await provider.CompleteAsync(working, tools, cancellationToken);
        sw.Stop();
        timings.Add($"llm {sw.ElapsedMilliseconds} ms");
        AgentCostCalculator.Compute(llmResult.Usage, options);
        AgentCostCalculator.Accumulate(totalUsage, llmResult.Usage);

        if (llmResult.Error != null) return BuildError(prior, timings, totalUsage, options, llmResult.Error);
        if (string.IsNullOrWhiteSpace(llmResult.AssistantJson)) return BuildError(prior, timings, totalUsage, options, "Assistant returned an empty response.");

        if (!TryParseAssistantObject(llmResult.AssistantJson, out var assistantObj, out var parseErr))
            return BuildError(prior, timings, totalUsage, options, parseErr!);

        var toolCalls = assistantObj!["tool_calls"]?.AsArray();
        if (isFinalCall || toolCalls == null || toolCalls.Count == 0)
            return BuildSuccess(llmResult.AssistantJson, prior, [], timings, totalUsage, options);

        var hasServerTool = toolCalls.Any(tc => _serverToolMap.ContainsKey(TryGetString(tc?["function"]?["name"]) ?? ""));
        var hasUiTool = toolCalls.Any(tc => !_serverToolMap.ContainsKey(TryGetString(tc?["function"]?["name"]) ?? ""));
        if (!hasServerTool)
            return BuildSuccess(llmResult.AssistantJson, prior, [], timings, totalUsage, options);

        var serverResults = await RunServerToolBatchAsync(toolCalls, guard, timings, cancellationToken);
        if (hasUiTool || hop == MaxServerHops)
            return BuildSuccess(llmResult.AssistantJson, prior, serverResults, timings, totalUsage, options);

        working.Add(assistantObj);
        prior.Add(llmResult.AssistantJson);
        foreach (var resJson in serverResults)
        {
            working.Add(JsonNode.Parse(resJson)!.AsObject());
            prior.Add(resJson);
        }
        return null;
    }

    private bool TryParseAssistantObject(string json, out JsonObject? obj, out string? error)
    {
        obj = null;
        error = null;
        try
        {
            obj = JsonNode.Parse(json)?.AsObject();
            if (obj == null) { error = "Assistant returned non-object JSON."; return false; }
            return true;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid JSON returned by LLM provider.");
            error = "Assistant returned invalid JSON.";
            return false;
        }
    }

    private async Task<List<string>> RunServerToolBatchAsync(
        JsonArray toolCalls,
        AgentServerGuard guard,
        List<string> timings,
        CancellationToken ct)
    {
        var results = new List<string>();
        foreach (var tc in toolCalls)
        {
            var fnName = TryGetString(tc?["function"]?["name"]) ?? "";
            if (!_serverToolMap.TryGetValue(fnName, out var handler)) continue;

            var callId = TryGetString(tc?["id"]) ?? Guid.NewGuid().ToString("N");
            var argsRaw = TryGetString(tc?["function"]?["arguments"]) ?? "{}";
            var sw = Stopwatch.StartNew();
            var resultJson = await ExecuteServerToolAsync(handler, argsRaw, guard, ct);
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
        AgentServerGuard guard,
        CancellationToken ct)
    {
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
        var blocked = guard.CheckBeforeExecute(handler.Name, argsRaw, argsDoc.RootElement);
        if (blocked != null) return blocked;

        var result = await handler.ExecuteAsync(argsDoc.RootElement, ct);
        return guard.RecordAndCheckError(handler.Name, result);
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

    public static bool TryParseMessages(IReadOnlyList<string>? rawMessages, out List<JsonObject> parsed) =>
        AgentTranscriptValidator.TryParseMessages(rawMessages, MaxTranscriptMessages, out parsed);

    private static string? TryGetString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static AgentStepResponseModel BuildSuccess(string assistant, List<string> prior, List<string> serverResults, List<string> timings, AgentUsageModel usage, AgentOptions options) =>
        new() { Assistant = assistant, Prior = prior, ServerToolResults = serverResults, Timings = timings, Usage = usage, MaxTaskCostUsd = options.MaxTaskCostUsd, MaxTaskModelCalls = options.MaxTaskModelCalls };

    private static AgentStepResponseModel BuildError(List<string> prior, List<string> timings, AgentUsageModel usage, AgentOptions options, string error) =>
        new() { Prior = prior, Timings = timings, Usage = usage, Error = error, MaxTaskCostUsd = options.MaxTaskCostUsd, MaxTaskModelCalls = options.MaxTaskModelCalls };
}
