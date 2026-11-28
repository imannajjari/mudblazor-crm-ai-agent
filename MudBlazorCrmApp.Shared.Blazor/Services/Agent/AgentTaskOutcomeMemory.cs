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

    public static List<AgentTaskOutcome> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var items = JsonSerializer.Deserialize<List<AgentTaskOutcome>>(json);
            return items?.Where(i => !string.IsNullOrWhiteSpace(i.Goal)).TakeLast(MaxStored).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IReadOnlyList<AgentTaskOutcome> items) => JsonSerializer.Serialize(items);

    public static void Add(List<AgentTaskOutcome> items, string goal, string outcome, string? reason)
    {
        items.Add(new AgentTaskOutcome(Clip(goal, MaxGoalChars), outcome, Clip(reason ?? string.Empty, MaxReasonChars)));
        if (items.Count > MaxStored) items.RemoveRange(0, items.Count - MaxStored);
    }

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
        return single.Length > max ? single[..max] : single;
    }
}
