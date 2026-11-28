using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazorCrmApp.Services.Agent;
using Xunit;

namespace MudBlazorCrmApp.Tests;

/// <summary>
/// Phase 1: provider retry/backoff and max_tokens truncation detection
/// (reference: RTProSL AnthropicAgentLlmProvider MAX_ATTEMPTS=3, MAX_TIMEOUT_ATTEMPTS=2).
/// </summary>
public class AgentProviderRetryTests
{
    private static readonly JsonObject UserMsg = new() { ["role"] = "user", ["content"] = "hi" };

    private sealed class SequenceHandler(HttpStatusCode[] statuses, string[] bodies) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var i = Math.Min(Calls, statuses.Length - 1);
            Calls++;
            var response = new HttpResponseMessage(statuses[i]);
            if (bodies[i].Length > 0)
                response.Content = new StringContent(bodies[i], Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(responder(request, cancellationToken));
        }
    }

    private static (OpenAiCompatibleAgentProvider Provider, SequenceHandler Handler) OpenAiPair(
        HttpStatusCode[] statuses, string[] bodies, AgentOptions? opts = null)
    {
        var handler = new SequenceHandler(statuses, bodies);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/v1/") };
        var options = opts ?? new AgentOptions { ApiKey = "sk-test", ProviderRetryDelayMs = 0 };
        return (new OpenAiCompatibleAgentProvider(
            client, Options.Create(options), NullLogger<OpenAiCompatibleAgentProvider>.Instance), handler);
    }

    private static (AnthropicAgentLlmProvider Provider, SequenceHandler Handler) AnthropicPair(
        HttpStatusCode[] statuses, string[] bodies, AgentOptions? opts = null)
    {
        var handler = new SequenceHandler(statuses, bodies);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/v1/") };
        var options = opts ?? new AgentOptions { ApiKey = "sk-test", ProviderRetryDelayMs = 0 };
        return (new AnthropicAgentLlmProvider(
            client, Options.Create(options), NullLogger<AnthropicAgentLlmProvider>.Instance), handler);
    }

    private const string OkChatBody = """
        {"choices":[{"message":{"role":"assistant","content":"Done"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":10,"completion_tokens":5}}
        """;

    [Fact]
    public async Task OpenAi_Retries429_ThenSucceeds()
    {
        var (provider, handler) = OpenAiPair(
            [HttpStatusCode.TooManyRequests, HttpStatusCode.OK],
            ["", OkChatBody]);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("Done", result.AssistantJson);
    }

    [Fact]
    public async Task OpenAi_DoesNotRetry400()
    {
        var (provider, handler) = OpenAiPair(
            [HttpStatusCode.BadRequest],
            [""]);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("Model server returned HTTP 400.", result.Error);
    }

    [Fact]
    public async Task OpenAi_GivesUpAfterMaxAttempts_OnPersistent503()
    {
        var (provider, handler) = OpenAiPair(
            [HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable],
            ["", "", "", ""]);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Equal(3, handler.Calls); // default MaxProviderAttempts = 3
        Assert.Equal("Model server is temporarily busy (503). Please try again in a moment.", result.Error);
    }

    [Fact]
    public async Task OpenAi_RespectsMaxProviderAttempts_Config()
    {
        var opts = new AgentOptions { ApiKey = "sk-test", ProviderRetryDelayMs = 0, MaxProviderAttempts = 5 };
        var (provider, handler) = OpenAiPair(
            [HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.OK],
            ["", "", "", "", OkChatBody], opts);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task OpenAi_TruncatedToolCall_ReturnsTruncationError()
    {
        const string body = """
            {"choices":[{"message":{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"set_field","arguments":"{\"id\":\"f1\",\"val"}}]},"finish_reason":"length"}],
             "usage":{"prompt_tokens":800,"completion_tokens":4096}}
            """;
        var (provider, handler) = OpenAiPair([HttpStatusCode.OK], [body]);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.NotNull(result.Error);
        Assert.Contains("cut off by the token limit", result.Error);
        Assert.Equal(4096, result.Usage.OutputTokens); // usage still counted
    }

    [Fact]
    public async Task OpenAi_TruncatedTextOnly_IsPassedThrough()
    {
        const string body = """
            {"choices":[{"message":{"role":"assistant","content":"partial answer"},"finish_reason":"length"}]}
            """;
        var (provider, _) = OpenAiPair([HttpStatusCode.OK], [body]);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Contains("partial answer", result.AssistantJson);
    }

    [Fact]
    public async Task Anthropic_Retries529_ThenSucceeds()
    {
        const string ok = """
            {"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn",
             "usage":{"input_tokens":10,"output_tokens":2}}
            """;
        var (provider, handler) = AnthropicPair(
            [(HttpStatusCode)529, HttpStatusCode.OK],
            ["", ok]);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Anthropic_TruncatedToolUse_ReturnsTruncationError()
    {
        const string body = """
            {"content":[{"type":"tool_use","id":"tu1","name":"set_field","input":{}}],"stop_reason":"max_tokens",
             "usage":{"input_tokens":900,"output_tokens":4096}}
            """;
        var (provider, handler) = AnthropicPair([HttpStatusCode.OK], [body]);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.NotNull(result.Error);
        Assert.Contains("cut off by the token limit", result.Error);
        Assert.Equal(900, result.Usage.InputTokens);   // usage survives for cost accounting
        Assert.Equal(4096, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task Anthropic_NotSentWhenCallerCancelled()
    {
        var (provider, handler) = AnthropicPair([HttpStatusCode.OK], ["{}"]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await provider.CompleteAsync([UserMsg], null, cts.Token);

        Assert.Equal(0, handler.Calls);
        Assert.Equal("Model server request was cancelled.", result.Error);
    }

    [Fact]
    public void Normalize_ClampsProviderAttemptSettings()
    {
        var bad = new AgentOptions { MaxProviderAttempts = 0, ProviderRetryDelayMs = -5 };
        bad.Normalize();
        Assert.Equal(3, bad.MaxProviderAttempts);
        Assert.Equal(700, bad.ProviderRetryDelayMs);

        var big = new AgentOptions { MaxProviderAttempts = 9, ProviderRetryDelayMs = 60_000 };
        big.Normalize();
        Assert.Equal(3, big.MaxProviderAttempts);
        Assert.Equal(700, big.ProviderRetryDelayMs);
    }

    [Fact]
    public void IsRetryable_MatchesReferenceStatuses()
    {
        foreach (var code in new[]
                 {
                     HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError,
                     HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable,
                     HttpStatusCode.GatewayTimeout, (HttpStatusCode)529
                 })
            Assert.True(AgentProviderRetry.IsRetryable(code), code.ToString());

        foreach (var code in new[]
                 {
                     HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.NotFound
                 })
            Assert.False(AgentProviderRetry.IsRetryable(code), code.ToString());
    }

    [Fact]
    public async Task OpenAi_HttpTimeout_RetriesUpToTimeoutCap_ThenTimesOut()
    {
        var handler = new FuncHandler((_, ct) => throw new TaskCanceledException());
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/v1/") };
        var provider = new OpenAiCompatibleAgentProvider(client,
            Options.Create(new AgentOptions { ApiKey = "sk-test", ProviderRetryDelayMs = 0 }),
            NullLogger<OpenAiCompatibleAgentProvider>.Instance);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Equal(2, handler.Calls); // MAX_TIMEOUT_ATTEMPTS = 2, fewer than the status cap of 3
        Assert.Equal("Model server request timed out.", result.Error);
    }

    [Fact]
    public async Task OpenAi_UnreachableConnection_RetriesUpToMax_ThenReportsUnreachable()
    {
        var handler = new FuncHandler((_, ct) => throw new HttpRequestException("connection refused"));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/v1/") };
        var provider = new OpenAiCompatibleAgentProvider(client,
            Options.Create(new AgentOptions { ApiKey = "sk-test", ProviderRetryDelayMs = 0 }),
            NullLogger<OpenAiCompatibleAgentProvider>.Instance);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Equal(3, handler.Calls);
        Assert.Equal("Model server is unreachable.", result.Error);
    }

    [Fact]
    public async Task Anthropic_401_IsNotRetried()
    {
        var (provider, handler) = AnthropicPair([HttpStatusCode.Unauthorized], [""]);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("Model server rejected the API key.", result.Error);
    }

    [Fact]
    public async Task OpenAi_GatewayNullsToolCalls_DoesNotThrow()
    {
        // Real gateways (LiteLLM) emit "tool_calls": null and "content": null shapes.
        const string body = """
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":null,"reasoning_content":"thinking..."},"finish_reason":"length"}]}
            """;
        var (provider, handler) = OpenAiPair([HttpStatusCode.OK], [body]);

        var result = await provider.CompleteAsync([UserMsg], null, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Null(result.Error); // not treated as tool truncation; passed through
        Assert.Contains("reasoning_content", result.AssistantJson);
    }

    [Fact]
    public async Task OpenAi_MalformedResponses_ReturnUnreadableError_NotException()
    {
        var (missingMessage, _) = OpenAiPair([HttpStatusCode.OK], ["""{"choices":[{}]}"""]);
        var r1 = await missingMessage.CompleteAsync([UserMsg], null, CancellationToken.None);
        Assert.Equal("Model server returned an unreadable response.", r1.Error);

        var (nonJson, _) = OpenAiPair([HttpStatusCode.OK], ["<html>gateway error</html>"]);
        var r2 = await nonJson.CompleteAsync([UserMsg], null, CancellationToken.None);
        Assert.Equal("Model server returned an unreadable response.", r2.Error);
    }

    [Fact]
    public async Task CancelDuringBackoff_StopsImmediately()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FuncHandler((_, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            cts.Cancel(); // stop pressed while the first failed response is being handled
            return response;
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/v1/") };
        var provider = new OpenAiCompatibleAgentProvider(client,
            Options.Create(new AgentOptions { ApiKey = "sk-test", ProviderRetryDelayMs = 5000 }),
            NullLogger<OpenAiCompatibleAgentProvider>.Instance);

        var result = await provider.CompleteAsync([UserMsg], null, cts.Token);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("Model server request was cancelled.", result.Error);
    }
}
