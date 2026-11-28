using System.Text;
using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Display model for the agent log, a port of the SLWEB reference app's
/// groupLog/groupSteps/stepTitle pipeline (AgentPanel.tsx, groupLog.ts, groupSteps.ts,
/// stepTitle.ts) adapted to this project's chat entry kinds.
/// Consecutive tool calls fold into one collapsed "steps" run; everything else is a line or a card.
/// </summary>
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

public static class AgentLogViewBuilder
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
                    if (entry.SubType == "answer")
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
            return ToolName(entry.Text) switch
            {
                "go_to_page" or "select_tab" or "select_tree_node" or "search_settings" => AgentWorkingMode.Navigate,
                "scan_screen" or "look_at_screen" or "scan_tabs" or "scan_menus" or "scan_context_menu" => AgentWorkingMode.Screen,
                "read_data" or "list_attachments" or "read_attachment" or "ask_attachment" => AgentWorkingMode.Data,
                "search_help" or "ask_help" or "get_help_page" or "lookup_term" or "ask_knowledge" => AgentWorkingMode.Help,
                "set_field" or "click_action" or "open_combo" or "close_combo" or "pick_option" or "clear_filters" or "sort_grid" or "right_click" => AgentWorkingMode.Write,
                "finish" => AgentWorkingMode.Finish,
                "ask_user" => AgentWorkingMode.Ask,
                _ => AgentWorkingMode.Think
            };
        }
        return AgentWorkingMode.Think;
    }

    private static string ToolName(string entryText)
    {
        var open = entryText.IndexOf('(');
        return open <= 0 ? entryText.Trim() : entryText[..open].Trim();
    }

    /// <summary>
    /// A readable caption for one tool call, the port of stepTitle.ts.
    /// Entry text has the form "name(argsJson)".
    /// </summary>
    public static string StepTitle(string entryText, AppText l)
    {
        var name = ToolName(entryText);
        var args = ParseArgs(entryText);

        string Arg(params string[] keys)
        {
            if (args == null) return string.Empty;
            foreach (var key in keys)
                if (args.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v!.Trim();
            return string.Empty;
        }
        string T(string key, string fallback, params string[] values)
        {
            var caption = l[key];
            if (string.IsNullOrWhiteSpace(caption) || caption == key) return fallback;
            try { return string.Format(caption, values); } catch (FormatException) { return fallback; }
        }

        var title = name switch
        {
            "go_to_page" => T("AGENT_STEP_OPEN_SCREEN", name, Arg("query", "name")),
            "click_action" => T("AGENT_STEP_CLICK_ACTION", name, Arg("id", "text")),
            "set_field" => T("AGENT_STEP_FILL", name, Arg("id", "field"), Arg("value")),
            "open_combo" => T("AGENT_STEP_OPEN_LIST", name, Arg("id", "field")),
            "close_combo" => T("AGENT_STEP_CLOSE_LIST", name),
            "pick_option" => T("AGENT_STEP_CHOOSE", name, Arg("text", "option")),
            "select_tab" => T("AGENT_STEP_OPEN_TAB", name, Arg("name", "id")),
            "clear_filters" => T("AGENT_STEP_CLEAR_FILTERS", name),
            "look_at_screen" => T("AGENT_STEP_LOOK_AT_SCREEN", name, Arg("question")),
            "lookup_term" or "ask_knowledge" => T("AGENT_STEP_LOOK_UP_NAME", name, Arg("term", "query")),
            "ask_user" => T("AGENT_STEP_ASK_YOU", name, Arg("question")),
            "search_help" => T("AGENT_STEP_SEARCH_HELP", name, Arg("query")),
            "ask_help" => T("AGENT_STEP_ASK_HELP", name, Arg("question")),
            "get_help_page" => T("AGENT_STEP_READ_HELP_PAGE", name, Arg("page", "path")),
            "read_data" => T("AGENT_STEP_READ_DATA", name, Arg("name", "source")),
            "scan_screen" => T("AGENT_STEP_SCAN_SCREEN", name),
            "scan_menus" => T("AGENT_STEP_SCAN_MENUS", name),
            "scan_tabs" => T("AGENT_STEP_SCAN_TABS", name),
            "scan_context_menu" => T("AGENT_STEP_SCAN_CONTEXT_MENU", name),
            "select_tree_node" => T("AGENT_STEP_OPEN_TREE_NODE", name, Arg("name", "text")),
            "search_settings" => T("AGENT_STEP_SEARCH_SETTINGS", name, Arg("text", "query")),
            "sort_grid" => T("AGENT_STEP_SORT_GRID", name, Arg("column", "id")),
            "right_click" => T("AGENT_STEP_RIGHT_CLICK", name, Arg("id")),
            "list_attachments" => T("AGENT_STEP_LIST_ATTACHMENTS", name),
            "read_attachment" => T("AGENT_STEP_READ_ATTACHMENT", name, Arg("fileName", "file")),
            "ask_attachment" => T("AGENT_STEP_ASK_ATTACHMENT", name, Arg("fileName", "file")),
            "finish" => T("AGENT_STEP_FINISH", name),
            _ => name
        };
        return Clip(title, 90);
    }

    private static string Clip(string value, int max)
    {
        value = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    private static Dictionary<string, string?>? ParseArgs(string entryText)
    {
        var open = entryText.IndexOf('(');
        if (open < 0 || !entryText.EndsWith(")", StringComparison.Ordinal)) return null;
        var json = entryText[(open + 1)..^1];
        if (json.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null
                };
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-renders a tool-call line so Persian arguments show as text instead of \uXXXX escapes
    /// (models/LiteLLM return escaped JSON in tool arguments). Falls back to the input when the
    /// argument block is not valid JSON.
    /// </summary>
    public static string DecodeToolText(string entryText)
    {
        if (string.IsNullOrEmpty(entryText) || !entryText.Contains("\\u", StringComparison.Ordinal))
            return entryText;
        var open = entryText.IndexOf('(', StringComparison.Ordinal);
        var close = entryText.LastIndexOf(')');
        if (open <= 0 || close <= open) return entryText;
        try
        {
            var args = System.Text.Json.Nodes.JsonNode.Parse(entryText[(open + 1)..close]);
            CleanJsonStrings(args);
            var rebuilt = string.Concat(entryText[..open], "(", args!.ToJsonString(DisplayJson), entryText[close..]);
            // ToJsonString re-escapes emoji/non-ASCII back to \uXXXX text; this is a display line,
            // not JSON a parser will read, so unescape whatever is left for a readable transcript.
            return rebuilt.Contains("\\u", StringComparison.Ordinal) ? UnescapeUnicode(rebuilt) : rebuilt;
        }
        catch (System.Text.Json.JsonException) { return entryText; }
    }

    /// <summary>
    /// Decodes leftover \uXXXX text inside JSON string values. Some models double-escape
    /// emoji/non-ASCII inside tool arguments (\\uXXXX survives one JSON parse as literal text).
    /// Numbers and other value types are left untouched.
    /// </summary>
    private static void CleanJsonStrings(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var prop in obj)
            {
                if (prop.Value is System.Text.Json.Nodes.JsonValue val && val.GetValueKind() == JsonValueKind.String)
                {
                    var s = val.GetValue<string>();
                    if (s is not null && s.Contains("\\u", StringComparison.Ordinal)) obj[prop.Key] = UnescapeUnicode(s);
                }
                else CleanJsonStrings(prop.Value);
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is System.Text.Json.Nodes.JsonValue val && val.GetValueKind() == JsonValueKind.String)
                {
                    var s = val.GetValue<string>();
                    if (s is not null && s.Contains("\\u", StringComparison.Ordinal)) arr[i] = UnescapeUnicode(s);
                }
                else CleanJsonStrings(arr[i]);
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex UnicodeEscape =
        new(@"\\u([0-9a-fA-F]{4})(\\u([0-9a-fA-F]{4}))?", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string UnescapeUnicode(string s) => UnicodeEscape.Replace(s, m =>
    {
        var c1 = (char)Convert.ToInt32(m.Groups[1].Value, 16);
        if (m.Groups[3].Success)
        {
            var c2 = (char)Convert.ToInt32(m.Groups[3].Value, 16);
            if (char.IsHighSurrogate(c1) && char.IsLowSurrogate(c2))
                return string.Concat(c1, c2);
        }
        return c1.ToString();
    });

    private static readonly JsonSerializerOptions DisplayJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Plain text of the whole log for the "copy chat" button (port of itemText.chatText).</summary>
    public static string ChatText(IReadOnlyList<AgentClientService.AgentChatEntry> entries, AppText l)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            var line = entry.Kind switch
            {
                "user" => entry.SubType == "answer"
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
