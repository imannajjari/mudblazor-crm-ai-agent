using System.Text.Json;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Server tool handler exposing direct BM25 help search (<c>search_help</c>) without extra LLM side calls.
/// </summary>
public sealed class SearchHelpServerTool(
    IAgentHelpCatalog helpCatalog,
    HelpSessionStore? sessionStore = null) : IServerToolHandler
{
    public string Name => "search_help";

    public Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var query = args.TryGetProperty("query", out var qEl) && qEl.ValueKind == JsonValueKind.String
            ? qEl.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(JsonSerializer.Serialize(new { ok = false, error = "Search query is required." }));

        var hits = helpCatalog.Search(query, maxResults: 4);
        var fresh = sessionStore != null && !string.IsNullOrEmpty(ctx.ChatKey)
            ? hits.Where(h => sessionStore.TryMarkSent(ctx.UserKey, ctx.ChatKey, h.Title)).ToList()
            : hits;

        if (fresh.Count == 0 && hits.Count > 0)
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                ok = true,
                alreadyShown = true,
                sections = hits.Select(h => new { h.Title, note = "Already retrieved earlier in this chat." })
            }));
        }

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            ok = true,
            sections = fresh.Select(h => new { h.Title, h.Score, content = h.Content })
        }));
    }
}
