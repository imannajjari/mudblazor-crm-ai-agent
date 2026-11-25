using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public sealed record AgentChatSummaryDto(string ChatKey, string Title, DateTime UpdatedDate);
public sealed record AgentChatDetailDto(string? ChatKey, string? Title, string? HistoryJson, string? LogJson, DateTime? UpdatedDate, int TaskCount);
public sealed record AgentChatListDto(List<AgentChatSummaryDto>? Chats);

public sealed class AgentChatStoreClient(
    HttpClient httpClient,
    IAccessTokenProvider tokenProvider,
    ILogger<AgentChatStoreClient> logger)
{
    public async Task<List<AgentChatSummaryDto>> ListAsync()
    {
        try
        {
            using var req = await CreateRequestAsync(HttpMethod.Get, "/api/agentchat");
            using var res = await httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return [];
            var payload = await res.Content.ReadFromJsonAsync<AgentChatListDto>();
            return payload?.Chats ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list saved agent chats.");
            return [];
        }
    }

    public async Task<AgentChatDetailDto?> GetAsync(string chatKey)
    {
        try
        {
            using var req = await CreateRequestAsync(HttpMethod.Get, $"/api/agentchat/{chatKey}");
            using var res = await httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadFromJsonAsync<AgentChatDetailDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load agent chat {ChatKey}.", chatKey);
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string chatKey)
    {
        try
        {
            using var req = await CreateRequestAsync(HttpMethod.Delete, $"/api/agentchat/{chatKey}");
            using var res = await httpClient.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete agent chat {ChatKey}.", chatKey);
            return false;
        }
    }

    public async Task SaveChatAndTaskAsync(
        string chatKey,
        int taskSeq,
        string goal,
        string summary,
        string historyJson,
        string transcriptJson,
        int inputTokens,
        int outputTokens,
        int cachedInputTokens,
        decimal cost,
        int modelCalls,
        int durationMs)
    {
        try
        {
            var title = goal.Length > 80 ? goal[..80] : goal;
            using var chatReq = await CreateRequestAsync(HttpMethod.Put, $"/api/agentchat/{chatKey}");
            chatReq.Content = JsonContent.Create(new { title, historyJson, logJson = "[]" });
            using var chatRes = await httpClient.SendAsync(chatReq);
            chatRes.EnsureSuccessStatusCode();

            using var taskReq = await CreateRequestAsync(HttpMethod.Put, $"/api/agentchat/{chatKey}/tasks/{taskSeq}");
            taskReq.Content = JsonContent.Create(new
            {
                goal,
                summary,
                transcriptJson,
                inputTokens,
                outputTokens,
                cachedInputTokens,
                cost,
                modelCalls,
                durationMs
            });
            using var taskRes = await httpClient.SendAsync(taskReq);
            taskRes.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist chat {ChatKey} task {TaskSeq}.", chatKey, taskSeq);
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string uri)
    {
        var req = new HttpRequestMessage(method, uri);
        var token = await tokenProvider.GetTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
