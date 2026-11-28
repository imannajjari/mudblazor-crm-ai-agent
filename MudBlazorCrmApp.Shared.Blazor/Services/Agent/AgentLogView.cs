using System.Text;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Display model for the agent log, a port of the SLWEB reference app's
/// groupLog/groupSteps/stepTitle pipeline (AgentPanel.tsx, groupLog.ts, groupSteps.ts,
/// stepTitle.ts) adapted to this project's chat entry kinds.
/// Consecutive tool calls fold into one collapsed "steps" run; everything else is a line or a card.
/// The builder is split across three files: grouping here, tool captions in
/// <c>AgentStepTitles.cs</c>, unicode-safe display decoding in <c>AgentToolTextCodec.cs</c>.
/// </summary>
public static partial class AgentLogViewBuilder
{
    public static IReadOnlyList<AgentLogView> Build(IReadOnlyList<AgentClientService.AgentChatEntry> entries, AppText l)
    {
        var views = new List<AgentLogView>();
        var run = new List<AgentStepItem>();
        var taskShown = false;

        void FlushRun()
        {
            if (run.Count == 0) return;
            views.Add(new AgentStepGroupView("group" + views.Count, run.ToArray()));
            run.Clear();
        }

        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case "tool":
                    run.Add(new AgentStepItem(entry, StepTitle(entry.Text, l)));
                    break;

                case "user":
                    FlushRun();
                    if (entry.SubType == AgentClientService.AgentChatEntry.SubTypeAnswer)
                        views.Add(new AgentLineView(entry, "user"));
                    else
                    {
                        views.Add(new AgentTaskView(entry, !taskShown));
                        taskShown = true;
                    }
                    break;

                case "assistant":
                    FlushRun();
                    views.Add(new AgentCardView(entry, "done"));
                    break;

                case "question":
                    FlushRun();
                    views.Add(new AgentCardView(entry, "ask"));
                    break;

                case "error":
                    FlushRun();
                    views.Add(new AgentCardView(entry, "error"));
                    break;

                case "help" or "note":
                    FlushRun();
                    views.Add(new AgentNotesView(entry));
                    break;

                default: // "info" and anything unknown
                    FlushRun();
                    views.Add(new AgentLineView(entry, "info"));
                    break;
            }
        }
        FlushRun();
        return views;
    }

    /// <summary>
    /// The working mark's mode, from the newest entry: like the reference workingMode.ts,
    /// the last tool call decides the animation; anything else means "thinking".
    /// </summary>
    public static AgentWorkingMode CurrentMode(IReadOnlyList<AgentClientService.AgentChatEntry> entries, bool waitingForUser)
    {
        if (waitingForUser) return AgentWorkingMode.Ask;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.Kind != "tool") continue;
            return ToolMetaFor(ToolName(entry.Text)).Mode;
        }
        return AgentWorkingMode.Think;
    }

    /// <summary>Plain text of the whole log for the "copy chat" button (port of itemText.chatText).</summary>
    public static string ChatText(IReadOnlyList<AgentClientService.AgentChatEntry> entries, AppText l)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            var line = entry.Kind switch
            {
                "user" => entry.SubType == AgentClientService.AgentChatEntry.SubTypeAnswer
                    ? $"{l["AGENT_YOU"]}: {entry.Text}"
                    : $"{l["AGENT_TASK"]}: {entry.Text}",
                "assistant" => entry.Text,
                "tool" => $"⚙ {StepTitle(entry.Text, l)}",
                "question" or "error" or "help" or "note" or "info" => entry.Text,
                _ => entry.Text
            };
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(line);
        }
        return sb.ToString();
    }
}
