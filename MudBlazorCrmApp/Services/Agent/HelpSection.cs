namespace MudBlazorCrmApp.Services.Agent;

public sealed record HelpSection(string Title, string Content, HashSet<string> Tokens);
