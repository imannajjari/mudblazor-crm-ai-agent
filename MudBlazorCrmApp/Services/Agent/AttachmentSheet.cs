namespace MudBlazorCrmApp.Services.Agent;

public sealed record AttachmentSheet(string Name, bool Hidden, List<string> Headers, List<string[]> Rows);
