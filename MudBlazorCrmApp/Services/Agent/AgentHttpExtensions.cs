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
}
