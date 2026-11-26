namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Custom modern SVG icon definitions for the AI Copilot UI.
/// </summary>
public static class AgentIcons
{
    /// <summary>
    /// Gemini-style 4-pointed star with a small orbit ring — used as the AI button icon.
    /// Renders as a crisp white path; gradient glow is applied via CSS on the button wrapper.
    /// </summary>
    public const string CopilotSvg =
        "<path d=\"M12 2C12 2 13.5 7.5 17 10C13.5 12.5 12 18 12 18C12 18 10.5 12.5 7 10C10.5 7.5 12 2 12 2Z\" fill=\"currentColor\" opacity=\"0.9\"/>" +
        "<path d=\"M12 6C12 6 13 9 15 10.5C13 12 12 15 12 15C12 15 11 12 9 10.5C11 9 12 6 12 6Z\" fill=\"currentColor\" opacity=\"0.3\"/>" +
        "<circle cx=\"19\" cy=\"5\" r=\"1.2\" fill=\"currentColor\" opacity=\"0.7\"/>" +
        "<circle cx=\"5\" cy=\"16\" r=\"0.9\" fill=\"currentColor\" opacity=\"0.5\"/>" +
        "<circle cx=\"18\" cy=\"17\" r=\"0.7\" fill=\"currentColor\" opacity=\"0.4\"/>";
}
