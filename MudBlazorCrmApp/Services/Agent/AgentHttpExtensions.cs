using System.Security.Claims;

namespace MudBlazorCrmApp.Services.Agent;

public static class AgentHttpExtensions
{
    public static string GetAgentUserKey(this ClaimsPrincipal user)
    {
        return user.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? user.Identity?.Name
               ?? throw new UnauthorizedAccessException("Authenticated user identifier is required.");
    }

    public static string GetAgentLanguage(this HttpContext? ctx)
    {
        var header = ctx?.Request.Headers["X-Agent-Language"].FirstOrDefault()
                     ?? ctx?.Request.Headers.AcceptLanguage.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(header) && header.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return "en";
        return "fa";
    }
}
