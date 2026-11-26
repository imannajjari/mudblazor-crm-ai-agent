using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MudBlazorCrmApp.Data;
using MudBlazorCrmApp.Models;
using MudBlazorCrmApp.Services.Agent;

namespace MudBlazorCrmApp.Controllers;

[Authorize]
[Route("api/[controller]")]
[ApiController]
public partial class AgentChatController(
    ApplicationDbContext db,
    AgentAttachmentService attachmentService,
    IOptions<AgentOptions> optionsAccessor) : ControllerBase
{
    private const int MaxCombinedTextChars = 3_000_000;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex ChatKeyRegex();

    [HttpGet("")]
    public async Task<ActionResult<AgentChatListResponse>> ListAsync(CancellationToken ct)
    {
        if (!optionsAccessor.Value.Enabled) return NotFound();

        var userId = User.GetAgentUserKey();
        var chats = await db.AgentChat
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedDate)
            .Take(200)
            .Select(c => new AgentChatSummaryDto(c.ChatKey, c.Title, c.UpdatedDate))
            .ToListAsync(ct);

        return Ok(new AgentChatListResponse(chats));
    }

    [HttpGet("{chatKey}")]
    public async Task<ActionResult<AgentChatDetailDto>> GetAsync(string chatKey, CancellationToken ct)
    {
        if (!optionsAccessor.Value.Enabled) return NotFound();
        if (!ChatKeyRegex().IsMatch(chatKey))
            return Ok(new AgentChatDetailDto(null, null, null, null, null, 0));

        var userId = User.GetAgentUserKey();
        var chat = await db.AgentChat
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ChatKey == chatKey, ct);

        if (chat == null)
            return Ok(new AgentChatDetailDto(null, null, null, null, null, 0));

        var taskCount = await db.AgentTask.CountAsync(t => t.UserId == userId && t.ChatKey == chatKey, ct);
        return Ok(new AgentChatDetailDto(chat.ChatKey, chat.Title, chat.HistoryJson, chat.LogJson, chat.UpdatedDate, taskCount));
    }

    [HttpPut("{chatKey}")]
    public async Task<ActionResult<AgentChatSaveResponse>> PutAsync(
        string chatKey,
        [FromBody] AgentChatSaveRequest request,
        CancellationToken ct)
    {
        if (!optionsAccessor.Value.Enabled) return NotFound();
        if (!ChatKeyRegex().IsMatch(chatKey))
            return Ok(new AgentChatSaveResponse("The chat key is not valid."));

        var historyJson = AgentAttachmentService.RedactAttachmentBlocks(request.HistoryJson ?? "[]");
        var logJson = AgentAttachmentService.RedactAttachmentBlocks(request.LogJson ?? "[]");
        var title = AgentAttachmentService.RedactAttachmentBlocks((request.Title ?? string.Empty).Trim());
        if (title.Length > 200) title = title[..200];
        if (title.Length + historyJson.Length + logJson.Length > MaxCombinedTextChars)
            return Ok(new AgentChatSaveResponse("The chat is too large to store."));

        var userId = User.GetAgentUserKey();
        var existing = await db.AgentChat
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ChatKey == chatKey, ct);

        if (existing == null)
        {
            db.AgentChat.Add(new AgentChat
            {
                UserId = userId,
                ChatKey = chatKey,
                Title = title,
                HistoryJson = historyJson,
                LogJson = logJson,
                CreatedDate = DateTime.UtcNow,
                UpdatedDate = DateTime.UtcNow
            });
        }
        else
        {
            existing.Title = title;
            existing.HistoryJson = historyJson;
            existing.LogJson = logJson;
            existing.UpdatedDate = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new AgentChatSaveResponse(null));
    }

    [HttpPut("{chatKey}/tasks/{taskSeq:int}")]
    public async Task<ActionResult<AgentChatSaveResponse>> SaveTaskAsync(
        string chatKey,
        int taskSeq,
        [FromBody] AgentChatTaskSaveRequest request,
        CancellationToken ct)
    {
        if (!optionsAccessor.Value.Enabled) return NotFound();
        if (!ChatKeyRegex().IsMatch(chatKey) || taskSeq < 1)
            return Ok(new AgentChatSaveResponse("The chat key is not valid."));

        var goal = AgentAttachmentService.RedactAttachmentBlocks(request.Goal ?? string.Empty);
        var summary = AgentAttachmentService.RedactAttachmentBlocks(request.Summary ?? string.Empty);
        var transcriptJson = AgentAttachmentService.RedactAttachmentBlocks(request.TranscriptJson ?? "[]");
        if (transcriptJson.Length + summary.Length + goal.Length > MaxCombinedTextChars)
            return Ok(new AgentChatSaveResponse("The chat is too large to store."));

        var userId = User.GetAgentUserKey();
        var existing = await db.AgentTask
            .FirstOrDefaultAsync(t => t.UserId == userId && t.ChatKey == chatKey && t.TaskSeq == taskSeq, ct);

        var safeCost = request.Cost is >= 0 and < 100000 ? request.Cost : 0m;
        var safeCached = request.CachedInputTokens >= 0 ? request.CachedInputTokens : 0;
        var started = DateTime.UtcNow.AddMilliseconds(-Math.Max(0, request.DurationMs));

        if (existing == null)
        {
            db.AgentTask.Add(new AgentTask
            {
                UserId = userId,
                ChatKey = chatKey,
                TaskSeq = taskSeq,
                Goal = goal,
                Summary = summary,
                TranscriptJson = transcriptJson,
                InputTokens = Math.Max(0, request.InputTokens),
                OutputTokens = Math.Max(0, request.OutputTokens),
                CachedInputTokens = safeCached,
                Cost = safeCost,
                ModelCalls = Math.Max(0, request.ModelCalls),
                DurationMs = Math.Max(0, request.DurationMs),
                StartedDate = started,
                CreatedDate = DateTime.UtcNow
            });
        }
        else
        {
            existing.Goal = goal;
            existing.Summary = summary;
            existing.TranscriptJson = transcriptJson;
            existing.InputTokens = Math.Max(0, request.InputTokens);
            existing.OutputTokens = Math.Max(0, request.OutputTokens);
            existing.CachedInputTokens = safeCached;
            existing.Cost = safeCost;
            existing.ModelCalls = Math.Max(0, request.ModelCalls);
            existing.DurationMs = Math.Max(0, request.DurationMs);
        }

        await db.SaveChangesAsync(ct);
        return Ok(new AgentChatSaveResponse(null));
    }

    [HttpDelete("{chatKey}")]
    public async Task<ActionResult<AgentChatSaveResponse>> DeleteAsync(string chatKey, CancellationToken ct)
    {
        if (!optionsAccessor.Value.Enabled) return NotFound();
        if (!ChatKeyRegex().IsMatch(chatKey))
            return Ok(new AgentChatSaveResponse("The chat key is not valid."));

        var userId = User.GetAgentUserKey();
        var chat = await db.AgentChat.FirstOrDefaultAsync(c => c.UserId == userId && c.ChatKey == chatKey, ct);
        if (chat != null) db.AgentChat.Remove(chat);

        var tasks = await db.AgentTask.Where(t => t.UserId == userId && t.ChatKey == chatKey).ToListAsync(ct);
        if (tasks.Count > 0) db.AgentTask.RemoveRange(tasks);

        await db.SaveChangesAsync(ct);
        attachmentService.DeleteChat(chatKey, userId);
        return Ok(new AgentChatSaveResponse(null));
    }

    public sealed record AgentChatSummaryDto(string ChatKey, string Title, DateTime UpdatedDate);
    public sealed record AgentChatListResponse(List<AgentChatSummaryDto> Chats);
    public sealed record AgentChatDetailDto(string? ChatKey, string? Title, string? HistoryJson, string? LogJson, DateTime? UpdatedDate, int TaskCount);
    public sealed record AgentChatSaveRequest(string? Title, string? HistoryJson, string? LogJson);
    public sealed record AgentChatTaskSaveRequest(string? Goal, string? Summary, string? TranscriptJson, int InputTokens, int OutputTokens, int CachedInputTokens, decimal Cost, int ModelCalls, int DurationMs);
    public sealed record AgentChatSaveResponse(string? Error);
}
