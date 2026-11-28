using System.Text.Encodings.Web;
using System.Text.Json;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

/// <summary>
/// Per-chat memory of how earlier tasks ended (reference taskRecord/taskMessage). Stored as JSON
/// in the chat's LogJson column and injected, after the goal, into the next task message.
/// </summary>
public static class AgentTaskOutcomeMemory
{
    public const string Done = "done";
    public const string Blocked = "blocked";
    public const string Stopped = "stopped";
    public const string Error = "error";

    public const int MaxStored = 10;
    public const int MaxInjected = 6;
    private const int MaxGoalChars = 120;
    private const int MaxReasonChars = 160;

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static List<AgentTaskOutcome> Parse(string? json)
    {
        var result = new List<AgentTaskOutcome>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var items = JsonSerializer.Deserialize<List<AgentTaskOutcome?>>(json);
            if (items == null) return result;
            foreach (var item in items)
                if (item is not null && !string.IsNullOrWhiteSpace(item.Goal))
                    Add(result, item.Goal, item.Outcome, item.Reason);
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IReadOnlyList<AgentTaskOutcome> items) => JsonSerializer.Serialize(items, WriteOptions);

    public static void Add(List<AgentTaskOutcome> items, string goal, string? outcome, string? reason)
    {
        items.Add(new AgentTaskOutcome(Clip(goal, MaxGoalChars), Known(outcome), Clip(reason ?? string.Empty, MaxReasonChars)));
        if (items.Count > MaxStored) items.RemoveRange(0, items.Count - MaxStored);
    }

    private static string Known(string? outcome) => outcome switch
    {
        Done or Blocked or Stopped or Error => outcome,
        _ => Error
    };

    public static string Block(IReadOnlyList<AgentTaskOutcome> items)
    {
        if (items.Count == 0) return string.Empty;
        var lines = items.TakeLast(MaxInjected).Select(i =>
            $"- {i.Goal} -> {i.Outcome}" + (string.IsNullOrEmpty(i.Reason) ? string.Empty : $" ({i.Reason})"));
        return "Recent outcomes:\n" + string.Join("\n", lines);
    }

    private static string Clip(string text, int max)
    {
        var single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (single.Length <= max) return single;
        var cut = char.IsHighSurrogate(single[max - 1]) ? max - 1 : max;
        return single[..cut];
    }
}
