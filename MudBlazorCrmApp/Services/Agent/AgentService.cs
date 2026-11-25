using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MudBlazorCrmApp.Services.Agent;

public sealed class AgentService(
    IAgentLlmProvider provider,
    AgentDataService dataService,
    AgentHelpService helpService,
    IOptions<AgentOptions> optionsAccessor)
{
    private const int MaxTranscriptMessages = 120;
    private const int MaxServerHops = 4;
    private const int MaxLookTextChars = 30_000;
    private const int MaxLookQuestionChars = 2_000;
    private static readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> RateWindows = new();
    private static readonly HashSet<string> ServerTools = new(StringComparer.Ordinal)
    {
        "read_data", "ask_help", "get_help_page"
    };

    public async Task<AgentStepResponseModel> StepAsync(
        AgentStepRequestModel request,
        string userKey,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        options.Normalize();

        if (!CheckRateLimit(userKey, options.MaxCallsPerUserPerMinute))
        {
            return new AgentStepResponseModel
            {
                Error = "Too many agent requests in a minute. Wait a moment and try again."
            };
        }

        if (!TryParseMessages(request.Messages, out var parsedMessages))
        {
            return new AgentStepResponseModel
            {
                Error = "The conversation sent to the agent is not valid."
            };
        }

        var working = new List<JsonObject>(parsedMessages.Count + 1)
        {
            new()
            {
                ["role"] = "system",
                ["content"] = AgentPrompts.MainSystemPrompt
            }
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
            ComputeCost(llmResult.Usage, options);
            AccumulateUsage(totalUsage, llmResult.Usage);

            if (llmResult.Error != null)
            {
                return new AgentStepResponseModel
                {
                    Prior = prior,
                    Timings = timings,
                    Usage = totalUsage,
                    Error = llmResult.Error,
                    MaxTaskCostUsd = options.MaxTaskCostUsd,
                    MaxTaskModelCalls = options.MaxTaskModelCalls
                };
            }

            if (string.IsNullOrWhiteSpace(llmResult.AssistantJson))
            {
                return new AgentStepResponseModel
                {
                    Prior = prior,
                    Timings = timings,
                    Usage = totalUsage,
                    Error = "Assistant returned an empty response.",
                    MaxTaskCostUsd = options.MaxTaskCostUsd,
                    MaxTaskModelCalls = options.MaxTaskModelCalls
                };
            }

            var assistantObj = JsonNode.Parse(llmResult.AssistantJson)?.AsObject();
            if (assistantObj == null)
            {
                return new AgentStepResponseModel
                {
                    Prior = prior,
                    Timings = timings,
                    Usage = totalUsage,
                    Error = "Assistant returned invalid JSON.",
                    MaxTaskCostUsd = options.MaxTaskCostUsd,
                    MaxTaskModelCalls = options.MaxTaskModelCalls
                };
            }

            var toolCalls = assistantObj["tool_calls"]?.AsArray();
            if (toolCalls == null || toolCalls.Count == 0)
            {
                return new AgentStepResponseModel
                {
                    Assistant = llmResult.AssistantJson,
                    Prior = prior,
                    Timings = timings,
                    Usage = totalUsage,
                    MaxTaskCostUsd = options.MaxTaskCostUsd,
                    MaxTaskModelCalls = options.MaxTaskModelCalls
                };
            }

            var hasServerTool = false;
            var hasUiTool = false;
            foreach (var tc in toolCalls)
            {
                var fnName = tc?["function"]?["name"]?.GetValue<string>() ?? "";
                if (ServerTools.Contains(fnName)) hasServerTool = true;
                else hasUiTool = true;
            }

            if (!hasServerTool)
            {
                return new AgentStepResponseModel
                {
                    Assistant = llmResult.AssistantJson,
                    Prior = prior,
                    Timings = timings,
                    Usage = totalUsage,
                    MaxTaskCostUsd = options.MaxTaskCostUsd,
                    MaxTaskModelCalls = options.MaxTaskModelCalls
                };
            }

            var serverResults = new List<string>();
            foreach (var tc in toolCalls)
            {
                var callId = tc?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                var fnName = tc?["function"]?["name"]?.GetValue<string>() ?? "";
                if (!ServerTools.Contains(fnName)) continue;

                var argsRaw = tc?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                var toolSw = Stopwatch.StartNew();
                var resultJson = await ExecuteServerToolAsync(fnName, argsRaw, seenServerCalls, cancellationToken);
                toolSw.Stop();
                timings.Add($"{fnName} {toolSw.ElapsedMilliseconds} ms");

                var toolMsg = new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = callId,
                    ["content"] = resultJson
                };
                serverResults.Add(toolMsg.ToJsonString());
            }

            if (hasUiTool || hop == MaxServerHops)
            {
                return new AgentStepResponseModel
                {
                    Assistant = llmResult.AssistantJson,
                    Prior = prior,
                    ServerToolResults = serverResults,
                    Timings = timings,
                    Usage = totalUsage,
                    MaxTaskCostUsd = options.MaxTaskCostUsd,
                    MaxTaskModelCalls = options.MaxTaskModelCalls
                };
            }

            working.Add(assistantObj);
            prior.Add(llmResult.AssistantJson);
            foreach (var resJson in serverResults)
            {
                var resObj = JsonNode.Parse(resJson)!.AsObject();
                working.Add(resObj);
                prior.Add(resJson);
            }
        }

        return new AgentStepResponseModel
        {
            Prior = prior,
            Timings = timings,
            Usage = totalUsage,
            Error = "Server tool hop limit reached.",
            MaxTaskCostUsd = options.MaxTaskCostUsd,
            MaxTaskModelCalls = options.MaxTaskModelCalls
        };
    }

    private async Task<string> ExecuteServerToolAsync(
        string name,
        string argsRaw,
        HashSet<string> seenCalls,
        CancellationToken ct)
    {
        var signature = $"{name}:{argsRaw.Trim()}";
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

        using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsRaw) ? "{}" : argsRaw);
        var args = argsDoc.RootElement;

        switch (name)
        {
            case "read_data":
                return await dataService.ReadAsync(args, ct);

            case "ask_help":
            {
                var q = args.TryGetProperty("question", out var qEl) ? qEl.GetString() ?? "" : "";
                var hits = helpService.Search(q);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    covered = hits.Count > 0,
                    sections = hits.Select(h => new { h.Title, h.Content })
                });
            }

            case "get_help_page":
            {
                var t = args.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
                var hit = helpService.GetPage(t);
                return hit == null
                    ? JsonSerializer.Serialize(new { ok = false, error = $"Help section '{t}' not found." })
                    : JsonSerializer.Serialize(new { ok = true, title = hit.Title, content = hit.Content });
            }

            default:
                return JsonSerializer.Serialize(new { ok = false, error = $"Unknown server tool '{name}'." });
        }
    }

    private static HashSet<string> CollectPriorServerCalls(IReadOnlyList<JsonObject> messages)
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
                if (!ServerTools.Contains(name)) continue;
                var args = tc?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                set.Add($"{name}:{args.Trim()}");
            }
        }
        return set;
    }

    public async Task<AgentLookResponseModel> LookAsync(
        AgentLookRequestModel request,
        string userKey,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        options.Normalize();

        if (!CheckRateLimit(userKey, options.MaxCallsPerUserPerMinute))
        {
            return new AgentLookResponseModel
            {
                Error = "Too many agent requests in a minute. Wait a moment and try again."
            };
        }

        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > MaxLookQuestionChars)
        {
            return new AgentLookResponseModel
            {
                Error = "The question is empty or too long."
            };
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return new AgentLookResponseModel
            {
                Error = "Visible screen text is required."
            };
        }

        var screenText = request.Text.Length > MaxLookTextChars
            ? request.Text[..MaxLookTextChars]
            : request.Text;

        var messages = new List<JsonObject>
        {
            new()
            {
                ["role"] = "system",
                ["content"] = AgentPrompts.LookSystemPrompt
            },
            new()
            {
                ["role"] = "user",
                ["content"] = $"Question: {request.Question}\n\nVisible screen text:\n{screenText}"
            }
        };

        var result = await provider.CompleteAsync(messages, tools: null, cancellationToken);
        ComputeCost(result.Usage, options);
        if (result.Error != null)
        {
            return new AgentLookResponseModel
            {
                Error = result.Error,
                Usage = result.Usage
            };
        }

        string? answerText = null;
        if (!string.IsNullOrWhiteSpace(result.AssistantJson))
        {
            using var doc = JsonDocument.Parse(result.AssistantJson);
            if (doc.RootElement.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            {
                answerText = contentEl.GetString();
            }
        }

        return new AgentLookResponseModel
        {
            Text = answerText ?? string.Empty,
            Usage = result.Usage
        };
    }

    private static void ComputeCost(AgentUsageModel usage, AgentOptions options)
    {
        if (!options.ShowCost || usage.Calls <= 0)
        {
            usage.Cost = null;
            return;
        }

        var uncachedInput = Math.Max(0, usage.InputTokens - usage.CachedInputTokens);
        var inputCost = (uncachedInput * options.InputPricePerMillionTokensUsd
                         + usage.CachedInputTokens * (options.InputPricePerMillionTokensUsd * 0.25m)) / 1_000_000m;
        var outputCost = usage.OutputTokens * options.OutputPricePerMillionTokensUsd / 1_000_000m;
        usage.Cost = Math.Round(inputCost + outputCost, 6);
    }

    private static void AccumulateUsage(AgentUsageModel total, AgentUsageModel add)
    {
        total.InputTokens += add.InputTokens;
        total.OutputTokens += add.OutputTokens;
        total.CachedInputTokens += add.CachedInputTokens;
        total.Calls += add.Calls;
        if (add.Cost.HasValue)
            total.Cost = (total.Cost ?? 0m) + add.Cost.Value;
    }

    public static bool TryParseMessages(IReadOnlyList<string>? rawMessages, out List<JsonObject> parsed)
    {
        parsed = [];
        if (rawMessages == null || rawMessages.Count == 0 || rawMessages.Count > MaxTranscriptMessages)
            return false;

        foreach (var raw in rawMessages)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(raw);
            }
            catch
            {
                return false;
            }

            if (node is not JsonObject obj)
                return false;

            var role = obj["role"]?.GetValue<string>();
            if (role is not ("user" or "assistant" or "tool"))
                return false;

            if (role == "tool" && string.IsNullOrWhiteSpace(obj["tool_call_id"]?.GetValue<string>()))
                return false;

            parsed.Add(obj);
        }

        return true;
    }

    private static bool CheckRateLimit(string userKey, int limitPerMinute)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-1);
        var queue = RateWindows.GetOrAdd(userKey, _ => new Queue<DateTimeOffset>());

        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() < cutoff)
                queue.Dequeue();

            if (queue.Count >= limitPerMinute)
                return false;

            queue.Enqueue(now);
            return true;
        }
    }
}
