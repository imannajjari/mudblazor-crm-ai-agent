using System.Net.Http;
using System.Security.Cryptography.X509Certificates;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Security gate for the agent's loopback self-HTTP calls (read_data). The "Loopback" HttpClient may
/// trust any certificate ONLY when the request target itself is a loopback URL — the traffic then
/// never leaves the machine and the Kestrel dev cert cannot chain to a public CA. Non-loopback
/// requests fail closed (normal strict TLS validation).
/// </summary>
public static class AgentLoopbackPolicy
{
    public static bool AcceptLoopbackCertificate(HttpRequestMessage? request, X509Certificate2? certificate) =>
        request?.RequestUri is { IsLoopback: true } && certificate is not null;
}
