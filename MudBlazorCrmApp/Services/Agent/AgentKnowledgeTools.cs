using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MudBlazorCrmApp.Services.Agent;

public sealed class RememberFactTool(AgentKnowledgeService kb, IOptions<AgentOptions> opts) : IServerToolHandler
{
    public string Name => "remember";
    public Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var guardErr = AgentKnowledgeWriteGuard.CheckRememberAllowed(ctx, opts.Value);
        if (guardErr != null)
            return Task.FromResult(JsonSerializer.Serialize(new { ok = false, reason = guardErr }));

        var topic = args.TryGetProperty("topic", out var t) ? t.GetString() ?? "" : "";
        var fact = args.TryGetProperty("fact", out var f) ? f.GetString() ?? "" : "";
        return kb.RememberAsync(topic, fact, ctx.UserKey, ctx.Language, ct);
    }
}

public sealed class AskKnowledgeTool(AgentKnowledgeService kb, AgentUsageCounter usageCounter, IOptions<AgentOptions> opts) : IServerToolHandler
{
    public string Name => "ask_knowledge";
    public async Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var q = args.TryGetProperty("question", out var qEl) ? qEl.GetString() ?? "" : "";
        var (json, usage) = await kb.AskKnowledgeAsync(q, ctx.Language, ct);
        AgentCostCalculator.Compute(usage, opts.Value);
        usageCounter.Add(usage);
        return json;
    }
}

public sealed class CorrectFactTool(AgentKnowledgeService kb, IOptions<AgentOptions> opts) : IServerToolHandler
{
    public string Name => "correct_fact";
    public Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var id = ReadFactId(args);
        var guardErr = AgentKnowledgeWriteGuard.CheckFactMutationAllowed(id, ctx, opts.Value, checkMaxChanges: true);
        if (guardErr != null)
            return Task.FromResult(JsonSerializer.Serialize(new { ok = false, reason = guardErr }));

        var fact = args.TryGetProperty("fact", out var fEl) ? fEl.GetString() ?? "" : "";
        var reason = args.TryGetProperty("reason", out var rEl) ? rEl.GetString() ?? "" : "";
        return kb.CorrectFactAsync(id, fact, reason, ctx.UserKey, ct);
    }

    internal static long ReadFactId(JsonElement args) =>
        args.TryGetProperty("fact_id", out var idEl) && idEl.TryGetInt64(out var v) ? v : 0;
}

public sealed class DeleteFactTool(AgentKnowledgeService kb, IOptions<AgentOptions> opts) : IServerToolHandler
{
    public string Name => "delete_fact";
    public Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var id = CorrectFactTool.ReadFactId(args);
        var guardErr = AgentKnowledgeWriteGuard.CheckFactMutationAllowed(id, ctx, opts.Value, checkMaxChanges: true);
        if (guardErr != null)
            return Task.FromResult(JsonSerializer.Serialize(new { ok = false, reason = guardErr }));

        var reason = args.TryGetProperty("reason", out var rEl) ? rEl.GetString() ?? "" : "";
        return kb.DeleteFactAsync(id, reason, ctx.UserKey, ctx.IsAdmin, ct);
    }
}

public sealed class FactDidNotHelpTool(AgentKnowledgeService kb, IOptions<AgentOptions> opts) : IServerToolHandler
{
    public string Name => "fact_did_not_help";
    public Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var id = CorrectFactTool.ReadFactId(args);
        var guardErr = AgentKnowledgeWriteGuard.CheckFactMutationAllowed(id, ctx, opts.Value, checkMaxChanges: false);
        if (guardErr != null)
            return Task.FromResult(JsonSerializer.Serialize(new { ok = false, reason = guardErr }));

        return kb.MarkDidNotHelpAsync(id, ct);
    }
}
