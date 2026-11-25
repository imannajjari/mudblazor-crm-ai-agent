namespace MudBlazorCrmApp.Services.Agent;

public class AgentStepRequestModel
{
    public List<string> Messages { get; set; } = [];
    public string? ChatKey { get; set; }
}

public class AgentStepResponseModel
{
    public string? Assistant { get; set; }
    public List<string> Prior { get; set; } = [];
    public List<string> ServerToolResults { get; set; } = [];
    public List<string> Timings { get; set; } = [];
    public AgentUsageModel Usage { get; set; } = new();
    public string? Error { get; set; }
    public decimal? MaxTaskCostUsd { get; set; }
    public int? MaxTaskModelCalls { get; set; }
}

public class AgentUsageModel
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public int Calls { get; set; }
    public decimal? Cost { get; set; }
}

public class AgentLookRequestModel
{
    public string? Image { get; set; }
    public string? MediaType { get; set; }
    public string? Text { get; set; }
    public string Question { get; set; } = string.Empty;
}

public class AgentLookResponseModel
{
    public string? Text { get; set; }
    public string? Error { get; set; }
    public AgentUsageModel Usage { get; set; } = new();
}

public class AgentStatusModel
{
    public bool UseAgent { get; set; }
    public int MaxTaskSteps { get; set; } = 25;
    public int MaxPriorTurns { get; set; } = 80;
    public int MaxTaskModelCalls { get; set; } = 40;
    public decimal MaxTaskCostUsd { get; set; } = 0.80m;
    public bool ShowCost { get; set; } = true;
}
