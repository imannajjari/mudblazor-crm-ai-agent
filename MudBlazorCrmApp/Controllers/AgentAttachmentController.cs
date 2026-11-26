using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MudBlazorCrmApp.Services.Agent;

namespace MudBlazorCrmApp.Controllers;

public sealed record AttachmentUploadRequest(string ChatKey, string FileName, string Content);

[Authorize]
[Route("api/agent/attachment")]
[ApiController]
public partial class AgentAttachmentController(
    AgentAttachmentService attachmentService,
    AgentAttachmentPolicy policy) : ControllerBase
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex ChatKeyRegex();
    [HttpPost]
    [RequestSizeLimit(15 * 1024 * 1024)]
    public async Task<IActionResult> UploadAsync([FromBody] AttachmentUploadRequest request, CancellationToken ct)
    {
        if (!policy.IsEnabled) return NotFound();
        var userKey = User.GetAgentUserKey();
        var result = await attachmentService.UploadAsync(userKey, request.ChatKey ?? "", request.FileName ?? "", request.Content ?? "", ct);
        return Ok(result);
    }

    [HttpGet("{id}")]
    public IActionResult GetAttachment(string id)
    {
        if (!policy.IsEnabled) return NotFound();
        var rec = attachmentService.GetForUser(id, User.GetAgentUserKey());
        if (rec == null || rec.RawBytes == null) return NotFound();
        return Ok(new
        {
            id = rec.Id,
            fileName = rec.FileName,
            contentType = rec.MediaType ?? "application/octet-stream",
            content = Convert.ToBase64String(rec.RawBytes)
        });
    }

    [HttpGet("chat/{chatKey}")]
    public IActionResult ListChat(string chatKey)
    {
        if (!policy.IsEnabled) return NotFound();
        if (!ChatKeyRegex().IsMatch(chatKey ?? "")) return BadRequest(new { error = "The chat key is not valid." });
        var ids = attachmentService.ListForChat(User.GetAgentUserKey(), chatKey!).Select(a => a.Id).ToList();
        return Ok(new { ids });
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        if (!policy.IsEnabled) return NotFound();
        var removed = attachmentService.Delete(id, User.GetAgentUserKey());
        return Ok(new { ok = removed });
    }

    [HttpDelete("chat/{chatKey}")]
    public IActionResult DeleteChat(string chatKey)
    {
        if (!policy.IsEnabled) return NotFound();
        if (!ChatKeyRegex().IsMatch(chatKey ?? "")) return BadRequest(new { error = "The chat key is not valid." });
        var count = attachmentService.DeleteChat(chatKey!, User.GetAgentUserKey());
        return Ok(new { ok = true, removed = count });
    }
}
