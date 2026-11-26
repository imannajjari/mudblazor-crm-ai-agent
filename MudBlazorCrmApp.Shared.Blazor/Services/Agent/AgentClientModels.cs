using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public sealed record AttachedFileItem(string Id, string FileName, string Kind, long SizeBytes, DateTime ExpiresUtc, string? Error = null);

internal sealed class AttachmentUploadResponseDto
{
    public string? Id { get; set; }
    public string? FileName { get; set; }
    public string? Kind { get; set; }
    public long SizeBytes { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string? Error { get; set; }
}

internal sealed class TaskRunState(string chatKey, int taskSeq, DateTimeOffset startedAt)
{
    public string ChatKey { get; } = chatKey;
    public int TaskSeq { get; } = taskSeq;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public int Calls { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public decimal Cost { get; set; }
}

internal sealed class AgentStatusDto
{
    public bool UseAgent { get; set; }
    public int MaxTaskSteps { get; set; } = 25;
    public int MaxPriorTurns { get; set; } = 80;
    public int MaxTaskModelCalls { get; set; } = 40;
    public decimal MaxTaskCostUsd { get; set; } = 0.80m;
    public bool ShowCost { get; set; } = true;
}

internal sealed class AgentStepResponseDto
{
    public string? Assistant { get; set; }
    public List<string>? Prior { get; set; }
    public List<string>? ServerToolResults { get; set; }
    public AgentUsageDto? Usage { get; set; }
    public string? Error { get; set; }
    public decimal? MaxTaskCostUsd { get; set; }
    public int? MaxTaskModelCalls { get; set; }
}

internal sealed class AgentUsageDto
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public int Calls { get; set; }
    public decimal? Cost { get; set; }
}

internal sealed class AgentStepApiClient(HttpClient httpClient, IAccessTokenProvider tokenProvider)
{
    public Task<AgentStatusDto?> GetStatusAsync() =>
        httpClient.GetFromJsonAsync<AgentStatusDto>("/api/agent/status");

    public async Task<AgentStepResponseDto?> PostStepAsync(
        List<string> messages,
        TaskRunState run,
        CancellationToken ct)
    {
        var payload = new
        {
            messages,
            chatKey = run.ChatKey,
            modelCallsSoFar = run.Calls,
            costSoFarUsd = run.Cost
        };
        using var res = await SendAuthorizedAsync(HttpMethod.Post, "/api/agent/step", JsonContent.Create(payload), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<AgentStepResponseDto>(cancellationToken: ct);
    }

    public async Task<AttachmentUploadResponseDto?> UploadAttachmentAsync(string chatKey, string fileName, byte[] bytes, CancellationToken ct)
    {
        var payload = new { chatKey, fileName, content = Convert.ToBase64String(bytes) };
        using var res = await SendAuthorizedAsync(HttpMethod.Post, "/api/agent/attachment", JsonContent.Create(payload), ct);
        if (!res.IsSuccessStatusCode)
        {
            try
            {
                var errBody = await res.Content.ReadFromJsonAsync<AttachmentUploadResponseDto>(cancellationToken: ct);
                return errBody ?? new AttachmentUploadResponseDto { Error = $"HTTP {(int)res.StatusCode}" };
            }
            catch (System.Text.Json.JsonException)
            {
                return new AttachmentUploadResponseDto { Error = $"HTTP {(int)res.StatusCode}" };
            }
        }
        return await res.Content.ReadFromJsonAsync<AttachmentUploadResponseDto>(cancellationToken: ct);
    }

    public async Task DeleteAttachmentAsync(string id, CancellationToken ct)
    {
        using var _ = await SendAuthorizedAsync(HttpMethod.Delete, $"/api/agent/attachment/{Uri.EscapeDataString(id)}", null, ct);
    }

    public async Task DeleteChatAttachmentsAsync(string chatKey, CancellationToken ct)
    {
        using var _ = await SendAuthorizedAsync(HttpMethod.Delete, $"/api/agent/attachment/chat/{Uri.EscapeDataString(chatKey)}", null, ct);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        var token = await tokenProvider.GetTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpClient.SendAsync(req, ct);
    }
}
