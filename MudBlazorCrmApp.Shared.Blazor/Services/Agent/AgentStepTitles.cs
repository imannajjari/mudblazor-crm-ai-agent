using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Tool-call display captions for the agent log (port of stepTitle.ts). The one <see cref="Tools"/>
/// table is the single source for both the working-mark mode and the caption/args mapping,
/// so the two can never drift apart.
/// </summary>
public static partial class AgentLogViewBuilder
{
    private const int TitleMaxLen = 90;

    private sealed record ToolMeta(AgentWorkingMode Mode, string? CaptionKey, string[][]? ArgKeyGroups);

    private static readonly Dictionary<string, ToolMeta> Tools = new(StringComparer.Ordinal)
    {
        ["go_to_page"] = new(AgentWorkingMode.Navigate, "AGENT_STEP_OPEN_SCREEN", [["query", "name"]]),
        ["select_tab"] = new(AgentWorkingMode.Navigate, "AGENT_STEP_OPEN_TAB", [["name", "id"]]),
        ["select_tree_node"] = new(AgentWorkingMode.Navigate, "AGENT_STEP_OPEN_TREE_NODE", [["name", "text"]]),
        ["search_settings"] = new(AgentWorkingMode.Navigate, "AGENT_STEP_SEARCH_SETTINGS", [["text", "query"]]),

        ["scan_screen"] = new(AgentWorkingMode.Screen, "AGENT_STEP_SCAN_SCREEN", null),
        ["look_at_screen"] = new(AgentWorkingMode.Screen, "AGENT_STEP_LOOK_AT_SCREEN", [["question"]]),
        ["scan_tabs"] = new(AgentWorkingMode.Screen, "AGENT_STEP_SCAN_TABS", null),
        ["scan_menus"] = new(AgentWorkingMode.Screen, "AGENT_STEP_SCAN_MENUS", null),
        ["scan_context_menu"] = new(AgentWorkingMode.Screen, "AGENT_STEP_SCAN_CONTEXT_MENU", null),

        ["read_data"] = new(AgentWorkingMode.Data, "AGENT_STEP_READ_DATA", [["name", "source"]]),
        ["list_attachments"] = new(AgentWorkingMode.Data, "AGENT_STEP_LIST_ATTACHMENTS", null),
        ["read_attachment"] = new(AgentWorkingMode.Data, "AGENT_STEP_READ_ATTACHMENT", [["fileName", "file"]]),
        ["ask_attachment"] = new(AgentWorkingMode.Data, "AGENT_STEP_ASK_ATTACHMENT", [["fileName", "file"]]),

        ["search_help"] = new(AgentWorkingMode.Help, "AGENT_STEP_SEARCH_HELP", [["query"]]),
        ["ask_help"] = new(AgentWorkingMode.Help, "AGENT_STEP_ASK_HELP", [["question"]]),
        ["get_help_page"] = new(AgentWorkingMode.Help, "AGENT_STEP_READ_HELP_PAGE", [["page", "path"]]),
        ["lookup_term"] = new(AgentWorkingMode.Help, "AGENT_STEP_LOOK_UP_NAME", [["term", "query"]]),
        ["ask_knowledge"] = new(AgentWorkingMode.Help, "AGENT_STEP_LOOK_UP_NAME", [["term", "query"]]),

        ["set_field"] = new(AgentWorkingMode.Write, "AGENT_STEP_FILL", [["id", "field"], ["value"]]),
        ["click_action"] = new(AgentWorkingMode.Write, "AGENT_STEP_CLICK_ACTION", [["id", "text"]]),
        ["open_combo"] = new(AgentWorkingMode.Write, "AGENT_STEP_OPEN_LIST", [["id", "field"]]),
        ["close_combo"] = new(AgentWorkingMode.Write, "AGENT_STEP_CLOSE_LIST", null),
        ["pick_option"] = new(AgentWorkingMode.Write, "AGENT_STEP_CHOOSE", [["text", "option"]]),
        ["clear_filters"] = new(AgentWorkingMode.Write, "AGENT_STEP_CLEAR_FILTERS", null),
        ["sort_grid"] = new(AgentWorkingMode.Write, "AGENT_STEP_SORT_GRID", [["column", "id"]]),
        ["right_click"] = new(AgentWorkingMode.Write, "AGENT_STEP_RIGHT_CLICK", [["id"]]),

        ["finish"] = new(AgentWorkingMode.Finish, "AGENT_STEP_FINISH", null),
        ["ask_user"] = new(AgentWorkingMode.Ask, "AGENT_STEP_ASK_YOU", [["question"]]),
    };

    private static ToolMeta ToolMetaFor(string name) =>
        Tools.TryGetValue(name, out var meta) ? meta : UnknownMeta;

    private static readonly ToolMeta UnknownMeta = new(AgentWorkingMode.Think, null, null);

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
        // Parse is memoized (language-neutral); only the localisation format runs per render.
        var (name, args) = ParseCall(entryText);
        var meta = ToolMetaFor(name);

        string Arg(params string[] keys)
        {
            if (args == null) return string.Empty;
            foreach (var key in keys)
                if (args.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            return string.Empty;
        }
        string T(string key, string fallback, params string[] values)
        {
            var caption = l[key];
            if (string.IsNullOrWhiteSpace(caption) || caption == key) return fallback;
            try { return string.Format(caption, values); } catch (FormatException) { return fallback; }
        }

        var title = meta.CaptionKey == null
            ? name
            : T(meta.CaptionKey, name, meta.ArgKeyGroups?.Select(keys => Arg(keys)).ToArray() ?? []);
        return Clip(title, TitleMaxLen);
    }

    private static string Clip(string value, int max)
    {
        value = Whitespace.Replace(value, " ").Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // Language-neutral parse cache: the same "name(args)" text repeats on every re-render of
    // the log; decoding JSON per entry per state change was the hot path on long WASM transcripts.
    // The args dictionary is handed out read-only so no consumer can poison the shared cache entry.
    private static readonly ConcurrentDictionary<string, (string Name, IReadOnlyDictionary<string, string?>? Args)> ParseCache =
        new(StringComparer.Ordinal);

    private static (string Name, IReadOnlyDictionary<string, string?>? Args) ParseCall(string entryText)
    {
        if (ParseCache.TryGetValue(entryText, out var hit)) return hit;
        var parsed = ParseArgs(entryText);
        var result = (ToolName(entryText), (IReadOnlyDictionary<string, string?>?)parsed?
            .AsReadOnly() ?? new Dictionary<string, string?>());
        if (ParseCache.Count > 4000) ParseCache.Clear();
        ParseCache[entryText] = result;
        return result;
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
}
