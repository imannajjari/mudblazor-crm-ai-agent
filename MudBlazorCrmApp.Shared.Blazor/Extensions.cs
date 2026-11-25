using MudBlazorCrmApp.Shared.Blazor.Authorization;
using MudBlazorCrmApp.Shared.Blazor.Services;
using MudBlazorCrmApp.Shared.Blazor.Services.Agent;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace MudBlazorCrmApp.Shared.Blazor;

public static class Extensions
{
    public static void AddBlazorServices(this IServiceCollection services, string baseAddress)
    {
        services.AddScoped<AppService>();

        services.AddScoped(sp
            => new HttpClient { BaseAddress = new Uri(baseAddress) });

        services.AddAuthorizationCore();
        services.AddScoped<AuthenticationStateProvider, IdentityAuthenticationStateProvider>();
        services.AddScoped<ThemeService>();
        services.AddScoped<AppText>();
        services.AddScoped<IAccessTokenProvider, IdentityAccessTokenProvider>();
        services.AddScoped<AgentPermissionGate>();
        services.AddScoped<AgentChatStoreClient>();
        services.AddScoped<IUiToolHandler, ScanScreenHandler>();
        services.AddScoped<IUiToolHandler, GoToPageHandler>();
        services.AddScoped<IUiToolHandler, SelectTabHandler>();
        services.AddScoped<IUiToolHandler, SetFieldHandler>();
        services.AddScoped<IUiToolHandler, OpenComboHandler>();
        services.AddScoped<IUiToolHandler, PickOptionHandler>();
        services.AddScoped<IUiToolHandler, CloseComboHandler>();
        services.AddScoped<IUiToolHandler, ClickActionHandler>();
        services.AddScoped<IUiToolHandler, ReadGridHeadersHandler>();
        services.AddScoped<IUiToolHandler, SelectGridRowHandler>();
        services.AddScoped<IUiToolHandler, LookAtScreenHandler>();
        services.AddScoped<IUiToolHandler, LookupTermHandler>();
        services.AddScoped<IUiToolHandler, AskUserHandler>();
        services.AddScoped<IUiToolHandler, FinishHandler>();
        services.AddScoped<AgentUiToolDispatcher>();
        services.AddScoped<AgentClientService>();
        services.AddMudServices();
    }

    public static void AddBrowserStorageService(this IServiceCollection services)
    {
        services.AddBlazoredLocalStorage();
        services.AddScoped<IStorageService, BrowserStorageService>();
    }
}
