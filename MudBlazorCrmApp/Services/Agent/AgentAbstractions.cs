using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace MudBlazorCrmApp.Services.Agent;

public interface IAgentRateLimiter
{
    bool TryAcquire(string userKey, int limitPerMinute);
    bool TryAcquireWindow(string key, int limit, TimeSpan window) => TryAcquire(key, limit);
}

public interface IAgentDataReader
{
    Task<string> ReadAsync(JsonElement args, CancellationToken ct);
}

public sealed record HelpSearchHit(string Title, string Content, int Score);

public interface IAgentHelpCatalog
{
    List<HelpSearchHit> Search(string query, int maxResults = 3);
    HelpSearchHit? GetPage(string titleQuery);
}

public sealed record ServerToolContext(
    string UserKey,
    string ChatKey,
    string Language,
    bool IsAdmin,
    IReadOnlyList<JsonObject> Transcript);

public interface IServerToolHandler
{
    string Name { get; }
    Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct);
}

/// <summary>
/// Request-scoped accumulator for main and side-call LLM token usage and cost.
/// </summary>
public sealed class AgentUsageCounter
{
    private int _callsWithoutCost;
    public AgentUsageModel Total { get; } = new();

    public void Add(AgentUsageModel add)
    {
        Total.InputTokens += Math.Max(0, add.InputTokens);
        Total.OutputTokens += Math.Max(0, add.OutputTokens);
        Total.CachedInputTokens += Math.Max(0, add.CachedInputTokens);
        Total.Calls += Math.Max(0, add.Calls);
        if (add.Cost.HasValue)
        {
            if (_callsWithoutCost == 0)
                Total.Cost = (Total.Cost ?? 0m) + add.Cost.Value;
        }
        else if (add.Calls > 0)
        {
            _callsWithoutCost += add.Calls;
            Total.Cost = null;
        }
    }
}

public sealed class MemoryCacheRateLimiter(IMemoryCache cache) : IAgentRateLimiter
{
    public bool TryAcquire(string userKey, int limitPerMinute) =>
        TryAcquireWindow($"agent_rl:{userKey}", limitPerMinute, TimeSpan.FromMinutes(1));

    public bool TryAcquireWindow(string key, int limit, TimeSpan window)
    {
        var lazyQueue = cache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = window > TimeSpan.FromMinutes(5) ? window : TimeSpan.FromMinutes(5);
            return new Lazy<Queue<DateTimeOffset>>(() => new Queue<DateTimeOffset>());
        })!;

        var queue = lazyQueue.Value;
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - window;
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() < cutoff)
                queue.Dequeue();

            if (queue.Count >= limit)
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
