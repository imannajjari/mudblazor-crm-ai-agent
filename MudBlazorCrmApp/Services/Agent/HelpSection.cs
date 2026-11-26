namespace MudBlazorCrmApp.Services.Agent;

public sealed record HelpSection(string Title, string Content, Dictionary<string, int> TermFreqs);
