using System.ComponentModel.DataAnnotations;

namespace MudBlazorCrmApp.Models;

public class AgentChat
{
    public long Id { get; set; }

    [MaxLength(128)]
    public string UserId { get; set; } = string.Empty;

    [MaxLength(40)]
    public string ChatKey { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public string HistoryJson { get; set; } = "[]";
    public string LogJson { get; set; } = "[]";

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedDate { get; set; } = DateTime.UtcNow;
}

public class AgentTask
{
    public long Id { get; set; }

    [MaxLength(128)]
    public string UserId { get; set; } = string.Empty;

    [MaxLength(40)]
    public string ChatKey { get; set; } = string.Empty;

    public int TaskSeq { get; set; }

    [MaxLength(2000)]
    public string Goal { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;
    public string TranscriptJson { get; set; } = "[]";

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public decimal Cost { get; set; }
    public int ModelCalls { get; set; }
    public int DurationMs { get; set; }

    public DateTime StartedDate { get; set; } = DateTime.UtcNow;
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
