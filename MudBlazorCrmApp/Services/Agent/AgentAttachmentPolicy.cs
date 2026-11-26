using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Resolves whether agent file attachments are allowed (internal LLM servers by default, explicit override otherwise).
/// </summary>
public sealed class AgentAttachmentPolicy(IOptions<AgentOptions> optionsAccessor)
{
    public bool IsEnabled
    {
        get
        {
            var opts = optionsAccessor.Value;
            return opts.Enabled && (opts.AttachmentsEnabled ?? IsInternalServer(opts));
        }
    }

    public static bool IsInternalServer(AgentOptions opts)
    {
        if (string.Equals(opts.Provider, "Anthropic", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Uri.TryCreate(opts.BaseUrl, UriKind.Absolute, out var uri))
            return false;

        var host = uri.DnsSafeHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(host, out var addr))
        {
            var ip = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr;
            if (IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes();
            return ip.AddressFamily switch
            {
                AddressFamily.InterNetwork => b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168),
                AddressFamily.InterNetworkV6 => (b[0] & 0xFE) == 0xFC,
                _ => false
            };
        }
        return !host.Contains('.');
    }
}
