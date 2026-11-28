using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MudBlazorCrmApp.Services.Agent;

public sealed class AgentService(
    IAgentLlmProvider provider,
    IAgentRateLimiter rateLimiter,
    IEnumerable<IServerToolHandler> serverTools,
    IOptions<AgentOptions> optionsAccessor,
    ILogger<AgentService> logger,
    AgentHelpLlmAdvisor? helpAdvisor = null,
    AgentKnowledgeService? knowledgeService = null,
    IHttpContextAccessor? httpContextAccessor = null,
    AgentUsageCounter? usageCounter = null,
    IMemoryCache? memoryCache = null)
{
    private const int MaxTranscriptMessages = 400;
    private const int MaxServerHops = 4;
    private const int MaxLookTextChars = 30_000;
    private const int MaxLookQuestionChars = 2_000;
    private const int MaxLookImageBytes = 4 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, object> DailyQuotaLocks = new(StringComparer.Ordinal);

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

        if (!TryAcquireDailyQuota(userKey, options.MaxStepsPerDay))
            return new AgentStepResponseModel { Error = "Daily agent step limit reached. Try again tomorrow." };

        var totalUsage = new AgentUsageModel();
        var httpContext = httpContextAccessor?.HttpContext;
        var language = httpContext.GetAgentLanguage();
        var isAdmin = httpContext?.User?.IsInRole("Administrator") == true;
        var isHowTo = AgentServerGuard.IsHowToQuestion(parsedMessages);

        // Overall wall-clock cap for one /step call: with provider retries, a single step can
        // otherwise stack 5 LLM calls × (timeout×2 or attempts×backoff) without any deadline.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.MaxStepDurationSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var stepCt = linked.Token;

        var (systemPrompt, extraNotes) = await BuildInitialContextAsync(parsedMessages, isHowTo, language, totalUsage, options, stepCt);
        var working = new List<JsonObject>(parsedMessages.Count + extraNotes.Count + 1)
        {
            new() { ["role"] = "system", ["content"] = systemPrompt }
        };
        var prior = new List<string>();
        foreach (var note in extraNotes)
        {
            var noteMsg = new JsonObject { ["role"] = "user", ["content"] = note };
            working.Add(noteMsg);
            prior.Add(noteMsg.ToJsonString());
        }
        working.AddRange(parsedMessages);

        var timings = new List<string>();
        var guard = AgentServerGuard.FromTranscript(parsedMessages, _serverToolNames);
        var toolContext = new ServerToolContext(userKey, request.ChatKey ?? string.Empty, language, isAdmin, working);

        for (var hop = 0; hop <= MaxServerHops; hop++)
        {
            var hopResult = await RunSingleHopAsync(working, request, options, totalUsage, isHowTo, hop, guard, toolContext, prior, timings, stepCt);
            if (hopResult == null) continue;
            if (hopResult.Error != null && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                hopResult.Error = "The agent step took too long and was stopped. Try a narrower task.";
            return hopResult;
        }

        AccumulateCounter(totalUsage, usageCounter);
        return BuildError(prior, timings, totalUsage, options, "Server tool hop limit reached.");
    }

    private async Task<(string SystemPrompt, List<string> ExtraUserNotes)> BuildInitialContextAsync(
        IReadOnlyList<JsonObject> parsedMessages,
        bool isHowTo,
        string language,
        AgentUsageModel totalUsage,
        AgentOptions options,
        CancellationToken ct)
    {
        var extraNotes = new List<string>();
        var lastUser = parsedMessages.Count > 0 && TryGetString(parsedMessages[^1]["role"]) == "user"
            ? TryGetString(parsedMessages[^1]["content"]) ?? ""
            : "";
        var isFirstStepOfTask = parsedMessages.Count == 1 || lastUser.StartsWith("Goal:", StringComparison.OrdinalIgnoreCase);
        if (!isFirstStepOfTask)
            return (AgentPrompts.MainSystemPrompt, extraNotes);

        var firstUserMsg = AgentServerGuard.ExtractGoalText(lastUser.TrimStart());

        var helpTask = isHowTo && helpAdvisor != null
            ? helpAdvisor.BuildAutoHelpContextAsync(firstUserMsg, ct)
            : Task.FromResult<(string? Context, AgentUsageModel Usage)>((null, new AgentUsageModel()));
        var knowledgeTask = knowledgeService != null
            ? knowledgeService.BuildContextNotesAsync(firstUserMsg, language, ct)
            : Task.FromResult<(string? NotesMessage, string? TimingNote)>((null, null));

        await Task.WhenAll(helpTask, knowledgeTask);

        var (autoHelp, sideUsage) = await helpTask;
        AgentCostCalculator.Compute(sideUsage, options, options.FastModel);
        AgentCostCalculator.Accumulate(totalUsage, sideUsage);
        if (!string.IsNullOrWhiteSpace(autoHelp))
            extraNotes.Add($"Relevant CRM Guide Context (auto-retrieved):\n{autoHelp}");

        var (notes, _) = await knowledgeTask;
        if (!string.IsNullOrWhiteSpace(notes))
            extraNotes.Add(notes);

        return (AgentPrompts.MainSystemPrompt, extraNotes);
    }

    private async Task<AgentStepResponseModel?> RunSingleHopAsync(
        List<JsonObject> working,
        AgentStepRequestModel request,
        AgentOptions options,
        AgentUsageModel totalUsage,
        bool isHowTo,
        int hop,
        AgentServerGuard guard,
        ServerToolContext toolContext,
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

        if (llmResult.Error != null)
        {
            AccumulateCounter(totalUsage, usageCounter);
            return BuildError(prior, timings, totalUsage, options, llmResult.Error);
        }
        if (string.IsNullOrWhiteSpace(llmResult.AssistantJson))
        {
            AccumulateCounter(totalUsage, usageCounter);
            return BuildError(prior, timings, totalUsage, options, "Assistant returned an empty response.");
        }

        if (!TryParseAssistantObject(llmResult.AssistantJson, out var assistantObj, out var parseErr))
        {
            AccumulateCounter(totalUsage, usageCounter);
            return BuildError(prior, timings, totalUsage, options, parseErr!);
        }

        var toolCalls = assistantObj!["tool_calls"]?.AsArray();
        if (isFinalCall || toolCalls == null || toolCalls.Count == 0)
        {
            AccumulateCounter(totalUsage, usageCounter);
            return BuildSuccess(llmResult.AssistantJson, prior, [], timings, totalUsage, options);
        }

        var hasServerTool = toolCalls.Any(tc => _serverToolMap.ContainsKey(TryGetString(tc?["function"]?["name"]) ?? ""));
        var hasUiTool = toolCalls.Any(tc => !_serverToolMap.ContainsKey(TryGetString(tc?["function"]?["name"]) ?? ""));
        if (!hasServerTool)
        {
            AccumulateCounter(totalUsage, usageCounter);
            return BuildSuccess(llmResult.AssistantJson, prior, [], timings, totalUsage, options);
        }

        var serverResults = await RunServerToolBatchAsync(toolCalls, guard, toolContext, timings, cancellationToken);
        if (hasUiTool || hop == MaxServerHops)
        {
            AccumulateCounter(totalUsage, usageCounter);
            return BuildSuccess(llmResult.AssistantJson, prior, serverResults, timings, totalUsage, options);
        }

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
        ServerToolContext toolContext,
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
            var resultJson = await ExecuteServerToolAsync(handler, argsRaw, guard, toolContext, ct);
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
        ServerToolContext toolContext,
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

        var result = await handler.ExecuteAsync(argsDoc.RootElement, toolContext, ct);
        return guard.RecordAndCheckError(handler.Name, result);
    }

    public async Task<AgentLookResponseModel> LookAsync(
        AgentLookRequestModel request,
        string userKey,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        options.Normalize();

        if (!rateLimiter.TryAcquire($"look:{userKey}", options.MaxCallsPerUserPerMinute))
            return new AgentLookResponseModel { Error = "Too many agent requests in a minute. Wait a moment and try again." };

        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > MaxLookQuestionChars)
            return new AgentLookResponseModel { Error = "The question is empty or too long." };

        if (string.IsNullOrWhiteSpace(request.Text) && string.IsNullOrWhiteSpace(request.Image))
            return new AgentLookResponseModel { Error = "Visible screen text or image is required." };

        if (!string.IsNullOrEmpty(request.Text) && request.Text.Length > MaxLookTextChars)
            return new AgentLookResponseModel { Error = "Visible screen text is too long." };

        if (!TryAcquireDailyQuota(userKey, options.MaxStepsPerDay))
            return new AgentLookResponseModel { Error = "Daily agent step limit reached. Try again tomorrow." };

        var userContent = new JsonArray();
        var screenText = request.Text ?? "";
        userContent.Add(new JsonObject { ["type"] = "text", ["text"] = $"Question: {request.Question}\n\nVisible screen text:\n{screenText}" });

        if (!string.IsNullOrWhiteSpace(request.Image))
        {
            if (!options.Vision)
                return new AgentLookResponseModel { Error = "Vision mode is disabled." };

            var mediaType = string.IsNullOrWhiteSpace(request.MediaType) ? "image/png" : request.MediaType.Trim().ToLowerInvariant();
            if (mediaType is not ("image/png" or "image/jpeg"))
                return new AgentLookResponseModel { Error = "Only PNG and JPEG images are supported." };

            byte[] imageBytes;
            try
            {
                imageBytes = Convert.FromBase64String(request.Image);
            }
            catch (FormatException)
            {
                return new AgentLookResponseModel { Error = "Invalid base64 image payload." };
            }

            if (imageBytes.Length == 0 || imageBytes.Length > MaxLookImageBytes)
                return new AgentLookResponseModel { Error = "Image size exceeds the 4 MB limit." };

            userContent.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{mediaType};base64,{request.Image}" }
            });
        }

        var messages = new List<JsonObject>
        {
            new() { ["role"] = "system", ["content"] = AgentPrompts.LookSystemPrompt },
            new() { ["role"] = "user", ["content"] = string.IsNullOrWhiteSpace(request.Image) ? (JsonNode)JsonValue.Create($"Question: {request.Question}\n\nVisible screen text:\n{screenText}")! : userContent }
        };

        var lookModel = string.IsNullOrWhiteSpace(request.Image) ? options.FastModel : options.VisionModel;
        var result = await provider.CompleteAsync(messages, tools: null, lookModel, cancellationToken);
        AgentCostCalculator.Compute(result.Usage, options, lookModel);
        usageCounter?.Add(result.Usage);
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

    private static void AccumulateCounter(AgentUsageModel total, AgentUsageCounter? counter)
    {
        if (counter != null && counter.Total.Calls > 0)
            AgentCostCalculator.Accumulate(total, counter.Total);
    }

    private bool TryAcquireDailyQuota(string userKey, int maxStepsPerDay)
    {
        if (maxStepsPerDay <= 0) return true;
        if (memoryCache == null)
            return rateLimiter.TryAcquireWindow($"agent_day:{userKey}", maxStepsPerDay, TimeSpan.FromDays(1));
        var cacheKey = $"agent:daily:{userKey}:{DateTime.UtcNow:yyyyMMdd}";
        var userLock = DailyQuotaLocks.GetOrAdd(cacheKey, static _ => new object());
        lock (userLock)
        {
            var count = memoryCache.Get<int>(cacheKey);
            if (count >= maxStepsPerDay) return false;
            memoryCache.Set(cacheKey, count + 1, TimeSpan.FromDays(1));
            return true;
        }
    }

    private static AgentStepResponseModel BuildSuccess(string assistant, List<string> prior, List<string> serverResults, List<string> timings, AgentUsageModel usage, AgentOptions options) =>
        new() { Assistant = assistant, Prior = prior, ServerToolResults = serverResults, Timings = timings, Usage = usage, MaxTaskCostUsd = options.MaxTaskCostUsd, MaxTaskModelCalls = options.MaxTaskModelCalls };

    private static AgentStepResponseModel BuildError(List<string> prior, List<string> timings, AgentUsageModel usage, AgentOptions options, string error) =>
        new() { Prior = prior, Timings = timings, Usage = usage, Error = error, MaxTaskCostUsd = options.MaxTaskCostUsd, MaxTaskModelCalls = options.MaxTaskModelCalls };
}
