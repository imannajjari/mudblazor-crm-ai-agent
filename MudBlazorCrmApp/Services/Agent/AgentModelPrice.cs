namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Built-in per-million token USD pricing table for known OpenAI, Gemini, and Anthropic models.
/// </summary>
public static class AgentModelPrice
{
    /// <summary>
    /// CacheWritePerMillion prices Anthropic's cache_creation_input_tokens (1.25x input for
    /// the 5-minute ephemeral cache); -1 means "no separate cache write fee, bill at input rate".
    /// </summary>
    public sealed record ModelRates(
        decimal InputPerMillion,
        decimal OutputPerMillion,
        decimal CachedInputPerMillion,
        decimal CacheWritePerMillion = -1m)
    {
        public decimal EffectiveCacheWritePerMillion => CacheWritePerMillion >= 0m ? CacheWritePerMillion : InputPerMillion;
    }

    private static readonly (string Prefix, ModelRates Rates)[] KnownModels =
    [
        ("claude-sonnet-4", new(3.00m, 15.00m, 0.30m, 3.75m)),
        ("claude-3-7-sonnet", new(3.00m, 15.00m, 0.30m, 3.75m)),
        ("claude-3-5-sonnet", new(3.00m, 15.00m, 0.30m, 3.75m)),
        ("claude-3-5-haiku", new(0.80m, 4.00m, 0.08m, 1.00m)),
        ("claude-haiku-4", new(0.80m, 4.00m, 0.08m, 1.00m)),
        ("claude-opus-4", new(15.00m, 75.00m, 1.50m, 18.75m)),
        ("gpt-4o-mini", new(0.15m, 0.60m, 0.075m)),
        ("gpt-4o", new(2.50m, 10.00m, 1.25m)),
        ("gpt-4.1-mini", new(0.40m, 1.60m, 0.10m)),
        ("gpt-4.1", new(2.00m, 8.00m, 0.50m)),
        ("gemini-2.5-flash", new(0.15m, 0.60m, 0.0375m)),
        ("gemini-2.5-pro", new(1.25m, 10.00m, 0.3125m)),
        ("gemini-3.5-flash-lite", new(0.10m, 0.40m, 0.025m)),
        ("gemini-2.0-flash", new(0.10m, 0.40m, 0.025m))
    ];

    public static ModelRates Resolve(string? modelName, AgentOptions fallbackOptions)
    {
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            var norm = modelName.Trim().ToLowerInvariant();
            foreach (var (prefix, rates) in KnownModels)
            {
                if (norm.StartsWith(prefix, StringComparison.Ordinal))
                    return rates;
            }
        }
        return new ModelRates(
            fallbackOptions.InputPricePerMillionTokensUsd,
            fallbackOptions.OutputPricePerMillionTokensUsd,
            fallbackOptions.InputPricePerMillionTokensUsd * 0.25m);
    }
}
