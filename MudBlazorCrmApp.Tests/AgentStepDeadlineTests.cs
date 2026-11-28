using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazorCrmApp.Services.Agent;
using Xunit;

namespace MudBlazorCrmApp.Tests;

/// <summary>
/// Overall /step wall-clock deadline (AgentOptions.MaxStepDurationSeconds): a server tool or
/// provider that runs past the deadline must yield the friendly step-timeout error,
/// never an unhandled OperationCanceledException (HTTP 500 HTML to the WASM client).
/// </summary>
public class AgentStepDeadlineTests
{
    private sealed class DelayingReadDataTool(TimeSpan delay) : IServerToolHandler
    {
        public string Name => "read_data";

        public async Task<string> ExecuteAsync(JsonElement args, ServerToolContext ctx, CancellationToken ct)
        {
            await Task.Delay(delay, ct); // re-throws cancellation exactly like AgentDataService
            return """{"ok":true}""";
        }
    }

    private sealed class ToolCallProvider(string toolCallJson) : IAgentLlmProvider
    {
        public Task<LlmCallResult> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonElement[]? tools, CancellationToken cancellationToken) =>
            Task.FromResult(new LlmCallResult(toolCallJson, new AgentUsageModel { Calls = 1, InputTokens = 10, OutputTokens = 5 }, null));
    }

    [Fact]
    public async Task StepDeadline_ServerToolCancellation_ReturnsFriendlyError_NotThrow()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var opts = Options.Create(new AgentOptions { Enabled = true, MaxStepDurationSeconds = 1 });
        var provider = new ToolCallProvider(
            """{"role":"assistant","content":"","tool_calls":[{"id":"d1","type":"function","function":{"name":"read_data","arguments":"{\"source\":\"customers\"}"}}]}""");
        var svc = new AgentService(provider, new MemoryCacheRateLimiter(cache), [new DelayingReadDataTool(TimeSpan.FromSeconds(30))],
            opts, NullLogger<AgentService>.Instance, memoryCache: cache);

        var res = await svc.StepAsync(
            new AgentStepRequestModel { Messages = ["""{"role":"user","content":"Read customers"}"""] },
            "deadline-u1", CancellationToken.None);

        Assert.NotNull(res.Error);
        Assert.Contains("took too long", res.Error);
        Assert.True(res.Usage.Calls >= 1); // usage from the completed LLM hop survives the deadline
    }

    [Fact]
    public async Task StepDeadline_CallerCancellation_IsNotRewrittenAsTimeout()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var opts = Options.Create(new AgentOptions { Enabled = true, MaxStepDurationSeconds = 600 });
        var provider = new ToolCallProvider(
            """{"role":"assistant","content":"","tool_calls":[{"id":"d1","type":"function","function":{"name":"read_data","arguments":"{\"source\":\"customers\"}"}}]}""");
        var svc = new AgentService(provider, new MemoryCacheRateLimiter(cache), [new DelayingReadDataTool(TimeSpan.FromSeconds(30))],
            opts, NullLogger<AgentService>.Instance, memoryCache: cache);

        using var cts = new CancellationTokenSource();
        var task = svc.StepAsync(
            new AgentStepRequestModel { Messages = ["""{"role":"user","content":"Read customers"}"""] },
            "deadline-u2", cts.Token);
        cts.Cancel();

        // Caller aborts propagate as cancellation (the WASM client handles that separately);
        // what must NEVER happen is the deadline message or an HTML 500 for the WRONG reason.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void Normalize_ClampsStepDeadline()
    {
        var low = new AgentOptions { MaxStepDurationSeconds = 0 };
        low.Normalize();
        Assert.Equal(600, low.MaxStepDurationSeconds);

        var high = new AgentOptions { MaxStepDurationSeconds = 1801 };
        high.Normalize();
        Assert.Equal(600, high.MaxStepDurationSeconds);

        var ok = new AgentOptions { MaxStepDurationSeconds = 30 };
        ok.Normalize();
        Assert.Equal(30, ok.MaxStepDurationSeconds);
    }
}
