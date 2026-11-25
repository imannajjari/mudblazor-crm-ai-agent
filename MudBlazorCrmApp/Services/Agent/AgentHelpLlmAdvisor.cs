using System.Text.Json;
using System.Text.Json.Nodes;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Uses lightweight LLM side calls (without tools) to extract search keywords,
/// distill matched help sections, and provide step-1 auto-help context.
/// </summary>
public sealed class AgentHelpLlmAdvisor(
    IAgentLlmProvider provider,
    IAgentHelpCatalog helpCatalog,
    ILogger<AgentHelpLlmAdvisor> logger)
{
    public async Task<string> ExtractKeywordsAsync(string question, CancellationToken ct)
    {
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "system", ["content"] = AgentPrompts.HelpKeywordsSystemPrompt },
            new() { ["role"] = "user", ["content"] = question }
        };
        var (text, _) = await CompleteTextSideCallAsync(messages, ct);
        return string.IsNullOrWhiteSpace(text) ? question : text.Trim();
    }

    public async Task<string> DistillAsync(string question, IReadOnlyList<HelpSearchHit> hits, CancellationToken ct)
    {
        if (hits.Count == 0) return string.Empty;
        var joined = string.Join("\n\n---\n\n", hits.Select(h => $"## {h.Title}\n{h.Content}"));
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "system", ["content"] = AgentPrompts.HelpDistillSystemPrompt },
            new() { ["role"] = "user", ["content"] = $"Question: {question}\n\nGuide sections:\n{joined}" }
        };
        var (text, _) = await CompleteTextSideCallAsync(messages, ct);
        return string.IsNullOrWhiteSpace(text) ? hits[0].Content : text.Trim();
    }

    public async Task<(string? Summary, AgentUsageModel Usage)> BuildAutoHelpContextAsync(string userMessage, CancellationToken ct)
    {
        var total = new AgentUsageModel();
        if (string.IsNullOrWhiteSpace(userMessage)) return (null, total);
        var question = ExtractGoalLine(userMessage);
        var (keywords, kwUsage) = await CompleteTextSideCallAsync(
            [new() { ["role"] = "system", ["content"] = AgentPrompts.HelpKeywordsSystemPrompt }, new() { ["role"] = "user", ["content"] = question }], ct);
        AgentCostCalculator.Accumulate(total, kwUsage);

        var hits = helpCatalog.Search(string.IsNullOrWhiteSpace(keywords) ? question : keywords.Trim(), maxResults: 2);
        if (hits.Count == 0) return (null, total);

        var joined = string.Join("\n\n---\n\n", hits.Select(h => $"## {h.Title}\n{h.Content}"));
        var (summary, distUsage) = await CompleteTextSideCallAsync(
            [new() { ["role"] = "system", ["content"] = AgentPrompts.HelpDistillSystemPrompt }, new() { ["role"] = "user", ["content"] = $"Question: {question}\n\nGuide sections:\n{joined}" }], ct);
        AgentCostCalculator.Accumulate(total, distUsage);

        return (string.IsNullOrWhiteSpace(summary) ? hits[0].Content : summary.Trim(), total);
    }

    private static string ExtractGoalLine(string raw)
    {
        var line = raw.Split('\n').FirstOrDefault(l => l.StartsWith("Goal:", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(line) ? (raw.Length > 400 ? raw[..400] : raw) : line[5..].Trim();
    }

    private async Task<(string? Text, AgentUsageModel Usage)> CompleteTextSideCallAsync(List<JsonObject> messages, CancellationToken ct)
    {
        try
        {
            var result = await provider.CompleteAsync(messages, tools: null, ct);
            if (result.Error != null || string.IsNullOrWhiteSpace(result.AssistantJson))
                return (null, result.Usage);

            using var doc = JsonDocument.Parse(result.AssistantJson);
            if (doc.RootElement.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                return (cEl.GetString(), result.Usage);
            return (null, result.Usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Help LLM side call failed; falling back to local guide text.");
            return (null, new AgentUsageModel());
        }
    }
}
