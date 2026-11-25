using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace MudBlazorCrmApp.Services.Agent;

public interface IAgentRateLimiter
{
    bool TryAcquire(string userKey, int limitPerMinute);
}

public interface IAgentDataReader
{
    Task<string> ReadAsync(JsonElement args, CancellationToken ct);
}

public interface IAgentHelpCatalog
{
    List<AgentHelpService.HelpSearchHit> Search(string query, int maxResults = 3);
    AgentHelpService.HelpSearchHit? GetPage(string titleQuery);
}

public interface IServerToolHandler
{
    string Name { get; }
    Task<string> ExecuteAsync(JsonElement args, CancellationToken ct);
}

public sealed class MemoryCacheRateLimiter(IMemoryCache cache) : IAgentRateLimiter
{
    public bool TryAcquire(string userKey, int limitPerMinute)
    {
        var queue = cache.GetOrCreate($"agent_rl:{userKey}", entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(5);
            return new Queue<DateTimeOffset>();
        })!;

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-1);
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

public static class AgentCostCalculator
{
    private const decimal CachedTokenDiscount = 0.25m;
    private const decimal TokensPerMillion = 1_000_000m;

    public static void Compute(AgentUsageModel usage, AgentOptions options)
    {
        if (!options.ShowCost || usage.Calls <= 0)
        {
            usage.Cost = null;
            return;
        }

        var safeInput = Math.Max(0, usage.InputTokens);
        var safeOutput = Math.Max(0, usage.OutputTokens);
        var clampedCached = Math.Clamp(usage.CachedInputTokens, 0, safeInput);
        var uncachedInput = safeInput - clampedCached;

        var inputCost = (uncachedInput * options.InputPricePerMillionTokensUsd
                         + clampedCached * (options.InputPricePerMillionTokensUsd * CachedTokenDiscount)) / TokensPerMillion;
        var outputCost = safeOutput * options.OutputPricePerMillionTokensUsd / TokensPerMillion;
        usage.Cost = Math.Round(inputCost + outputCost, 6);
    }

    public static void Accumulate(AgentUsageModel total, AgentUsageModel add)
    {
        total.InputTokens += Math.Max(0, add.InputTokens);
        total.OutputTokens += Math.Max(0, add.OutputTokens);
        total.CachedInputTokens += Math.Max(0, add.CachedInputTokens);
        total.Calls += Math.Max(0, add.Calls);
        if (add.Cost.HasValue)
            total.Cost = (total.Cost ?? 0m) + add.Cost.Value;
    }
}
