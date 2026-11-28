using System.Net;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Shared retry/backoff plumbing for the LLM providers, mirroring the reference agent
/// (AnthropicAgentLlmProvider): retryable statuses 429/5xx/529, capped transport retries,
/// linear backoff of ProviderRetryDelayMs × attempt, and no retry after user cancellation.
/// </summary>
public static class AgentProviderRetry
{
    /// <summary>HTTP timeouts get fewer attempts than status retries (reference MAX_TIMEOUT_ATTEMPTS).</summary>
    public const int MaxTimeoutAttempts = 2;

    public static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or (HttpStatusCode)529;

    public static string FormatError(HttpStatusCode code, string? body, ILogger logger)
    {
        // Gateways sometimes echo request content back in error bodies; keep logs bounded.
        if (body is { Length: > 500 })
            body = body[..500] + "…";
        logger.LogWarning("LLM provider returned {StatusCode}: {Body}", (int)code, body);
        return code switch
        {
            HttpStatusCode.ServiceUnavailable => "Model server is temporarily busy (503). Please try again in a moment.",
            HttpStatusCode.TooManyRequests => "Model server rate limit reached (429). Please wait a moment.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Model server rejected the API key.",
            _ => $"Model server returned HTTP {(int)code}."
        };
    }

    /// <summary>
    /// Sends the request, rebuilding it on each attempt via <paramref name="requestFactory"/>
    /// (HttpRequestMessage cannot be resent), and retries retryable failures with linear backoff.
    /// </summary>
    public static async Task<ProviderSendResult> SendWithRetryAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        int maxAttempts,
        int retryDelayMs,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        maxAttempts = Math.Max(1, maxAttempts);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = requestFactory();
                using var response = await httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                    return new ProviderSendResult(true, response.StatusCode, body, null);

                var error = FormatError(response.StatusCode, body, logger);
                if (!IsRetryable(response.StatusCode) || attempt >= maxAttempts)
                    return new ProviderSendResult(false, response.StatusCode, body, error);

                logger.LogInformation("LLM provider attempt {Attempt}/{Max} got {StatusCode}; retrying.",
                    attempt, maxAttempts, (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Stop button / task abort: never retry.
                return new ProviderSendResult(false, 0, null, "Model server request was cancelled.");
            }
            catch (TaskCanceledException)
            {
                // HttpClient timeout (token not cancelled): the model gateway was too slow.
                if (attempt >= MaxTimeoutAttempts || attempt >= maxAttempts)
                {
                    logger.LogWarning("LLM provider timed out after {Attempt} attempt(s).", attempt);
                    return new ProviderSendResult(false, 0, null, "Model server request timed out.");
                }
                logger.LogInformation("LLM provider attempt {Attempt} timed out; retrying.", attempt);
            }
            catch (HttpRequestException ex)
            {
                // Connection dropped mid-body read surfaces as HttpRequestException with a
                // cancelled inner task; count it against the timeout cap, not the status cap.
                var isTimeout = ex.InnerException is TaskCanceledException;
                if (isTimeout && attempt >= MaxTimeoutAttempts)
                {
                    logger.LogWarning("LLM provider connection timed out after {Attempt} attempt(s).", attempt);
                    return new ProviderSendResult(false, 0, null, "Model server request timed out.");
                }
                if (attempt >= maxAttempts)
                {
                    logger.LogError(ex, "Error calling LLM provider.");
                    return new ProviderSendResult(false, 0, null, "Model server is unreachable.");
                }
                logger.LogInformation("LLM provider attempt {Attempt} failed: {Message}; retrying.", attempt, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error calling LLM provider.");
                return new ProviderSendResult(false, 0, null, "Model server is unreachable.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds((long)retryDelayMs * attempt), cancellationToken);
            }
            catch (TaskCanceledException)
            {
                return new ProviderSendResult(false, 0, null, "Model server request was cancelled.");
            }
        }
    }
}
