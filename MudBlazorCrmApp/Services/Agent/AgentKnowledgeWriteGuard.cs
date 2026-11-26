using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Enforces transcript-level knowledge write/edit guards (no learning from files, verified in-app work, shown fact IDs, max changes).
/// </summary>
public static partial class AgentKnowledgeWriteGuard
{
    public const string NotesMark = "Knowledge notes";
    private static readonly HashSet<string> ChangeTools = new(StringComparer.Ordinal) { "correct_fact", "delete_fact" };

    private static readonly HashSet<string> AppTools = new(StringComparer.Ordinal)
    {
        "go_to_page", "look_at_screen", "select_tab", "right_click", "scan_context_menu", "scan_menus",
        "scan_tabs", "scan_screen", "select_tree_node", "search_settings", "clear_filters", "sort_grid",
        "open_combo", "close_combo", "pick_option", "click_action", "set_field"
    };

    [GeneratedRegex(@"^- \[(\d+)\] ", RegexOptions.Multiline)]
    private static partial Regex NoteLinePattern();

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex BracketIdPattern();

    [GeneratedRegex(@"\[att:[0-9a-f]{32}\s+""[^""]*""")]
    private static partial Regex AttachmentMarkerPattern();

    public static bool HasAttachments(IReadOnlyList<JsonObject> messages) =>
        messages.Any(m => m["role"]?.GetValue<string>() == "user"
            && AttachmentMarkerPattern().IsMatch(m["content"]?.GetValue<string>() ?? ""));

    public static bool HasWorkedInApplication(IReadOnlyList<JsonObject> messages) =>
        GetToolNames(messages).Any(AppTools.Contains);

    public static bool HasCheckedInApplication(IReadOnlyList<JsonObject> messages) =>
        HasWorkedInApplication(messages) || GetToolNames(messages).Any(n => n == "read_data");

    public static int FactChanges(IReadOnlyList<JsonObject> messages) =>
        GetToolNames(messages).Count(ChangeTools.Contains);

    public static HashSet<long> ShownFactIds(IReadOnlyList<JsonObject> messages)
    {
        var ids = new HashSet<long>();
        foreach (var msg in messages)
        {
            var role = msg["role"]?.GetValue<string>();
            var content = msg["content"] is JsonValue cv && cv.TryGetValue<string>(out var s) ? s : null;
            if (string.IsNullOrEmpty(content)) continue;

            if (role == "user" && content.StartsWith(NotesMark, StringComparison.Ordinal))
                AddIds(ids, NoteLinePattern().Matches(content));
            else if (role == "tool" && content.Contains("\"fromKnowledge\":true", StringComparison.Ordinal))
                AddIds(ids, BracketIdPattern().Matches(content));
        }
        return ids;
    }

    public static string? CheckRememberAllowed(ServerToolContext ctx, AgentOptions opts)
    {
        if (opts.KnowledgeAdminOnly && !ctx.IsAdmin)
            return "You do not have permission to edit shared knowledge.";
        if (HasAttachments(ctx.Transcript))
            return "Knowledge cannot be saved during a task that uses attached files.";
        if (!HasWorkedInApplication(ctx.Transcript))
            return "Only save facts after verifying them directly in the application UI during this task.";
        return null;
    }

    public static string? CheckFactMutationAllowed(long factId, ServerToolContext ctx, AgentOptions opts, bool checkMaxChanges)
    {
        if (opts.KnowledgeAdminOnly && !ctx.IsAdmin)
            return "You do not have permission to edit shared knowledge.";
        if (!ShownFactIds(ctx.Transcript).Contains(factId))
            return $"Fact [{factId}] was not shown in this task and cannot be modified.";
        if (!HasCheckedInApplication(ctx.Transcript))
            return "Verify the fact against the application or read_data before updating or flagging it.";
        if (checkMaxChanges && FactChanges(ctx.Transcript) >= opts.KnowledgeMaxChangesPerTask)
            return $"At most {opts.KnowledgeMaxChangesPerTask} knowledge corrections or deletions are allowed per task.";
        return null;
    }

    private static IEnumerable<string> GetToolNames(IReadOnlyList<JsonObject> messages)
    {
        foreach (var msg in messages)
        {
            if (msg["role"]?.GetValue<string>() != "assistant" || msg["tool_calls"] is not JsonArray tcs) continue;
            foreach (var tc in tcs)
            {
                if (tc?["function"]?["name"] is JsonValue nv && nv.TryGetValue<string>(out var name))
                    yield return name;
            }
        }
    }

    private static void AddIds(HashSet<long> ids, MatchCollection matches)
    {
        foreach (Match m in matches)
        {
            if (long.TryParse(m.Groups[1].Value, out var id))
                ids.Add(id);
        }
    }
}
