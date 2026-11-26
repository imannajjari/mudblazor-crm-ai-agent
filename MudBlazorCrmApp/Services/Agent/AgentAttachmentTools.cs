using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MudBlazorCrmApp.Services.Agent;

public sealed class ListAttachmentsTool(AgentAttachmentService attachments) : IServerToolHandler
{
    public string Name => "list_attachments";
    public Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var list = attachments.ListForChat(ctx.UserKey, ctx.ChatKey)
            .Select(a => new { a.Id, a.FileName, a.Kind, a.SizeBytes, a.Summary, a.ExpiresUtc });
        return Task.FromResult(JsonSerializer.Serialize(new { ok = true, attachments = list }));
    }
}

public sealed class ReadAttachmentTool(AgentAttachmentService attachments) : IServerToolHandler
{
    public string Name => "read_attachment";
    public Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var id = args.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
        var rec = attachments.GetForChat(id, ctx.UserKey, ctx.ChatKey);
        if (rec == null)
            return Task.FromResult(JsonSerializer.Serialize(new { ok = false, attachmentGone = true, message = "The file is no longer available. Ask the user to upload it again." }));

        var sheet = args.TryGetProperty("sheet", out var s) ? s.GetString() : null;
        var fromRow = args.TryGetProperty("fromRow", out var fr) && fr.TryGetInt32(out var f) ? f : 0;
        var rowCount = args.TryGetProperty("rowCount", out var rc) && rc.TryGetInt32(out var r) ? r : 50;
        return Task.FromResult(AgentAttachmentService.ReadPagedRows(rec, sheet, fromRow, rowCount));
    }
}

public sealed class AskAttachmentTool(
    AgentAttachmentService attachments,
    AgentUsageCounter usageCounter) : IServerToolHandler
{
    public string Name => "ask_attachment";
    public async Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
    {
        var id = args.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
        var question = args.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
        var rec = attachments.GetForChat(id, ctx.UserKey, ctx.ChatKey);
        if (rec == null)
            return JsonSerializer.Serialize(new { ok = false, attachmentGone = true, message = "The file is no longer available. Ask the user to upload it again." });

        var (json, usage) = await attachments.AskAttachmentAsync(rec, question, ct);
        usageCounter.Add(usage);
        return json;
    }
}
