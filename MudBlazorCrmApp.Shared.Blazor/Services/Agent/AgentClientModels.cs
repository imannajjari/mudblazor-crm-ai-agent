using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

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
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/agent/step")
        {
            Content = JsonContent.Create(payload)
        };
        var token = await tokenProvider.GetTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var res = await httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<AgentStepResponseDto>(cancellationToken: ct);
    }
}
