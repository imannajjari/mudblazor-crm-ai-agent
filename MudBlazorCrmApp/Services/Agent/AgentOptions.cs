namespace MudBlazorCrmApp.Services.Agent;

public class AgentOptions
{
    public const string SectionName = "Agent";

    public bool Enabled { get; set; } = false;
    public string Provider { get; set; } = "OpenAiCompatible";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai/";
    public string Model { get; set; } = "gemini-3.5-flash-lite";
    public string? FastModel { get; set; }
    public string? VisionModel { get; set; }
    public string? ApiKey { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public bool Vision { get; set; } = true;
    public int MaxCallsPerUserPerMinute { get; set; } = 60;
    public int MaxStepsPerDay { get; set; } = 5000;
    public int MaxTaskModelCalls { get; set; } = 40;
    public int MaxTaskSteps { get; set; } = 25;
    public int MaxPriorTurns { get; set; } = 80;
    public decimal MaxTaskCostUsd { get; set; } = 0.80m;
    public int TimeoutSeconds { get; set; } = 90;
    public int HistoryDays { get; set; } = 90;
    public string? LoopbackBaseUrl { get; set; }
    public int MaxLoopbackResponseMB { get; set; } = 32;
    public bool? AttachmentsEnabled { get; set; }
    public bool KnowledgeAdminOnly { get; set; } = false;
    public int KnowledgeMaxChangesPerTask { get; set; } = 3;
    public decimal InputPricePerMillionTokensUsd { get; set; } = 0.10m;
    public decimal OutputPricePerMillionTokensUsd { get; set; } = 0.40m;
    public bool ShowCost { get; set; } = true;

    /// <summary>
    /// Applies self-correcting bounds so invalid config values fall back to safe defaults.
    /// </summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";
        if (!BaseUrl.EndsWith('/'))
            BaseUrl += "/";
        if (string.IsNullOrWhiteSpace(Model))
            Model = "gemini-3.5-flash-lite";
        if (MaxTokens <= 0 || MaxTokens > 32768)
            MaxTokens = 4096;
        if (MaxCallsPerUserPerMinute <= 0 || MaxCallsPerUserPerMinute > 300)
            MaxCallsPerUserPerMinute = 60;
        if (MaxStepsPerDay < 0 || MaxStepsPerDay > 50000)
            MaxStepsPerDay = 5000;
        if (MaxTaskModelCalls <= 0 || MaxTaskModelCalls > 100)
            MaxTaskModelCalls = 40;
        if (MaxTaskSteps <= 0 || MaxTaskSteps > 100)
            MaxTaskSteps = 25;
        if (MaxPriorTurns <= 0 || MaxPriorTurns > 200)
            MaxPriorTurns = 80;
        if (MaxTaskCostUsd <= 0 || MaxTaskCostUsd > 20m)
            MaxTaskCostUsd = 0.80m;
        if (TimeoutSeconds <= 0 || TimeoutSeconds > 300)
            TimeoutSeconds = 90;
        if (HistoryDays < 0 || HistoryDays > 3650)
            HistoryDays = 90;
        if (MaxLoopbackResponseMB <= 0 || MaxLoopbackResponseMB > 128)
            MaxLoopbackResponseMB = 32;
        if (KnowledgeMaxChangesPerTask <= 0 || KnowledgeMaxChangesPerTask > 10)
            KnowledgeMaxChangesPerTask = 3;
        if (InputPricePerMillionTokensUsd < 0)
            InputPricePerMillionTokensUsd = 0.10m;
        if (OutputPricePerMillionTokensUsd < 0)
            OutputPricePerMillionTokensUsd = 0.40m;
    }
}
