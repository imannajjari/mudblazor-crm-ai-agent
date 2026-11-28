namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>The "what is the agent doing" animation mode, port of the reference workingMode.ts.</summary>
public enum AgentWorkingMode
{
    Think,
    Screen,
    Data,
    Help,
    Write,
    Navigate,
    Finish,
    Ask
}

/// <summary>Base of the display model for one row of the agent log.</summary>
public abstract record AgentLogView;

/// <summary>A user task line (bold, top divider). Maps to the reference .agent-task kind "task".</summary>
public sealed record AgentTaskView(AgentClientService.AgentChatEntry Entry, bool IsFirst) : AgentLogView;

/// <summary>A plain tone-coloured line (.agent-line .agent-tone--x).</summary>
public sealed record AgentLineView(AgentClientService.AgentChatEntry Entry, string Tone) : AgentLogView;

/// <summary>A coloured card: "done" (final answer), "ask" (question) or "error".</summary>
public sealed record AgentCardView(AgentClientService.AgentChatEntry Entry, string CardType) : AgentLogView;

/// <summary>A collapsed help/knowledge context note (.agent-notes details).</summary>
public sealed record AgentNotesView(AgentClientService.AgentChatEntry Entry) : AgentLogView;

/// <summary>A single tool step inside a folded run (.agent-step details row).</summary>
public sealed record AgentStepItem(AgentClientService.AgentChatEntry Entry, string Title);

/// <summary>A run of consecutive tool steps folded into one line (.agent-steps).</summary>
public sealed record AgentStepGroupView(string Id, IReadOnlyList<AgentStepItem> Steps) : AgentLogView
{
    public int StepCount => Steps.Count;
    public bool HasWarning => Steps.Any(s => s.Entry.IsBlocked);
}
