using Microsoft.AspNetCore.Components.Authorization;
using MudBlazorCrmApp.Shared.Blazor.Authorization;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public interface IAccessTokenProvider
{
    Task<string?> GetTokenAsync();
}

public sealed class IdentityAccessTokenProvider(AuthenticationStateProvider authProvider) : IAccessTokenProvider
{
    public async Task<string?> GetTokenAsync()
    {
        if (authProvider is IdentityAuthenticationStateProvider idProvider)
            return await idProvider.GetBearerTokenAsync();
        return null;
    }
}
