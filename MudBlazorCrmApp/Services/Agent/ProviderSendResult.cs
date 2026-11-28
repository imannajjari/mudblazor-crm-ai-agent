using System.Net;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Result of a retried provider HTTP send: status + body on a completed response,
/// or a user-facing <see cref="Error"/> when every attempt failed.
/// </summary>
public sealed record ProviderSendResult(
    bool Ok,
    HttpStatusCode StatusCode,
    string? Body,
    string? Error);
