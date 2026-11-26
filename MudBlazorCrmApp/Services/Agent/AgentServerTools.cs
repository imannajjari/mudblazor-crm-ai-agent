using System.Text.Json;

namespace MudBlazorCrmApp.Services.Agent;

public sealed class ReadDataToolHandler(IAgentDataReader dataReader) : IServerToolHandler
{
    public string Name => "read_data";
    public Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct) =>
        dataReader.ReadAsync(args, ct);
}

public sealed class AskHelpToolHandler(IAgentHelpCatalog helpCatalog) : IServerToolHandler
{
    public string Name => "ask_help";

    public Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var q = args.TryGetProperty("question", out var qEl) ? qEl.GetString() ?? "" : "";
        var hits = helpCatalog.Search(q);
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            ok = true,
            covered = hits.Count > 0,
            sections = hits.Select(h => new { h.Title, h.Content })
        }));
    }
}

public sealed class GetHelpPageToolHandler(IAgentHelpCatalog helpCatalog) : IServerToolHandler
{
    public string Name => "get_help_page";

    public Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var t = args.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
        var hit = helpCatalog.GetPage(t);
        var json = hit == null
            ? JsonSerializer.Serialize(new { ok = false, error = $"Help section '{t}' not found." })
            : JsonSerializer.Serialize(new { ok = true, title = hit.Title, content = hit.Content });
        return Task.FromResult(json);
    }
}
