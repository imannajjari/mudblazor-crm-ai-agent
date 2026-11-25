using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MudBlazorCrmApp.Shared.Blazor.Services.Agent;

namespace MudBlazorCrmApp.Shared.Blazor.Services;

public sealed class AgentClientService(
    HttpClient httpClient,
    IAccessTokenProvider tokenProvider,
    AgentChatStoreClient chatStore,
    AgentPermissionGate permissionGate,
    AgentUiToolDispatcher toolDispatcher,
    AppText appText,
    ILogger<AgentClientService> logger) : IDisposable
{
    public event Action? OnStateChanged;

    private bool _isDrawerOpen;
    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        set
        {
            if (_isDrawerOpen != value)
            {
                _isDrawerOpen = value;
                NotifyChanged();
            }
        }
    }

    public AgentMode Mode { get; set; } = AgentMode.Manual;
    public bool IsRunning { get; private set; }
    public bool WaitingForUserAnswer { get; private set; }
    public string? PendingQuestion { get; private set; }
    public bool WaitingForPermission { get; private set; }
    public string? PendingPermissionLabel { get; private set; }
    public string? PendingPermissionRisk { get; private set; }

    public IReadOnlyList<AgentChatEntry> Entries => _entries;
    public List<AgentChatSummaryDto> SavedChats { get; private set; } = [];
    public string CurrentChatKey { get; private set; } = NewChatKey();
    public int TotalCalls { get; private set; }
    public int TotalInputTokens { get; private set; }
    public int TotalOutputTokens { get; private set; }
    public decimal TotalCost { get; private set; }
    public bool ShowCost { get; private set; } = true;
    public int MaxTaskSteps { get; private set; } = 25;
    public int MaxPriorTurns { get; private set; } = 80;
    public decimal MaxTaskCostUsd { get; private set; } = 0.80m;
    public int MaxTaskModelCalls { get; private set; } = 40;

    private readonly List<AgentChatEntry> _entries = [];
    private TaskCompletionSource<string>? _userAnswerTcs;
    private TaskCompletionSource<PermissionDecision>? _permissionTcs;
    private CancellationTokenSource? _cts;
    private Task? _activeRunTask;
    private int _nextTaskSeq = 1;

    public async Task<bool> IsEnabledAsync()
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<AgentStatusDto>("/api/agent/status");
            if (res == null) return false;
            if (res.MaxTaskSteps > 0) MaxTaskSteps = res.MaxTaskSteps;
            if (res.MaxPriorTurns > 0) MaxPriorTurns = res.MaxPriorTurns;
            if (res.MaxTaskModelCalls > 0) MaxTaskModelCalls = res.MaxTaskModelCalls;
            if (res.MaxTaskCostUsd > 0) MaxTaskCostUsd = res.MaxTaskCostUsd;
            ShowCost = res.ShowCost;
            return res.UseAgent;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query /api/agent/status.");
            return false;
        }
    }

    public async Task LoadChatsAsync()
    {
        SavedChats = await chatStore.ListAsync();
        NotifyChanged();
    }

    public async Task LoadChatAsync(string chatKey)
    {
        await StopAndWaitAsync();
        var detail = await chatStore.GetAsync(chatKey);
        if (detail == null || string.IsNullOrEmpty(detail.ChatKey)) return;

        CurrentChatKey = detail.ChatKey;
        _nextTaskSeq = Math.Max(1, detail.TaskCount + 1);
        _entries.Clear();
        if (!string.IsNullOrWhiteSpace(detail.HistoryJson))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<List<AgentChatEntry>>(detail.HistoryJson);
                if (loaded != null) _entries.AddRange(loaded);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to deserialize HistoryJson for {ChatKey}.", chatKey);
            }
        }
        permissionGate.ResetSession();
        ResetCounters();
        NotifyChanged();
    }

    public async Task DeleteChatAsync(string chatKey)
    {
        if (!await chatStore.DeleteAsync(chatKey)) return;
        SavedChats.RemoveAll(c => c.ChatKey == chatKey);
        if (CurrentChatKey == chatKey)
            await NewChatAsync();
        else
            NotifyChanged();
    }

    public async Task NewChatAsync()
    {
        await StopAndWaitAsync();
        CurrentChatKey = NewChatKey();
        _nextTaskSeq = 1;
        _entries.Clear();
        permissionGate.ResetSession();
        ResetCounters();
        NotifyChanged();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _userAnswerTcs?.TrySetCanceled();
        _permissionTcs?.TrySetCanceled();
    }

    private async Task StopAndWaitAsync()
    {
        Stop();
        if (_activeRunTask is { IsCompleted: false } t)
        {
            try { await t; }
            catch (OperationCanceledException) { }
        }
    }

    public void SubmitUserAnswer(string answer)
    {
        if (WaitingForUserAnswer && _userAnswerTcs != null)
        {
            WaitingForUserAnswer = false;
            PendingQuestion = null;
            _entries.Add(new AgentChatEntry("user", answer));
            NotifyChanged();
            _userAnswerTcs.TrySetResult(answer);
        }
    }

    public void ResolvePermission(PermissionDecision decision)
    {
        if (WaitingForPermission && _permissionTcs != null)
        {
            WaitingForPermission = false;
            PendingPermissionLabel = null;
            PendingPermissionRisk = null;
            NotifyChanged();
            _permissionTcs.TrySetResult(decision);
        }
    }

    public Task SendTaskAsync(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal) || IsRunning) return Task.CompletedTask;
        _activeRunTask = RunTaskCoreAsync(goal);
        return _activeRunTask;
    }

    private async Task RunTaskCoreAsync(string goal)
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        permissionGate.ClearTaskDeclines();

        var run = new TaskRunState(CurrentChatKey, _nextTaskSeq++, DateTimeOffset.UtcNow);
        var ledger = new List<string>();
        var priorTurns = _entries.Where(e => e.Kind is "user" or "assistant").TakeLast(MaxPriorTurns).ToList();

        _entries.Add(new AgentChatEntry("user", goal));
        NotifyChanged();

        var messages = new List<string>();
        var lastSummary = string.Empty;

        try
        {
            foreach (var turn in priorTurns)
                messages.Add(JsonSerializer.Serialize(new { role = turn.Kind, content = turn.Text }));

            var snapshotJson = await toolDispatcher.CaptureSnapshotAsync(ct);
            messages.Add(JsonSerializer.Serialize(new
            {
                role = "user",
                content = $"Goal: {goal}\nDate: {DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\nCurrent screen snapshot:\n{snapshotJson}"
            }));

            for (var step = 0; step < MaxTaskSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                if (run.Calls >= MaxTaskModelCalls || run.Cost >= MaxTaskCostUsd)
                {
                    _entries.Add(new AgentChatEntry("info", $"Task budget reached (calls: {run.Calls}/{MaxTaskModelCalls}, cost: ${run.Cost:0.0000}/${MaxTaskCostUsd:0.00})."));
                    break;
                }

                var stepRes = await PostStepAsync(messages, ct);
                if (stepRes == null) { _entries.Add(new AgentChatEntry("error", "Empty response from agent server.")); break; }

                if (stepRes.MaxTaskCostUsd is > 0) MaxTaskCostUsd = stepRes.MaxTaskCostUsd.Value;
                if (stepRes.MaxTaskModelCalls is > 0) MaxTaskModelCalls = stepRes.MaxTaskModelCalls.Value;
                AccumulateUsage(run, stepRes.Usage);

                if (!string.IsNullOrWhiteSpace(stepRes.Error)) { _entries.Add(new AgentChatEntry("error", stepRes.Error)); break; }
                if (string.IsNullOrWhiteSpace(stepRes.Assistant)) { _entries.Add(new AgentChatEntry("error", "Assistant returned an empty message.")); break; }

                if (stepRes.Prior is { Count: > 0 })
                {
                    foreach (var p in stepRes.Prior) { messages.Add(p); TryLogPriorMessage(p); }
                }

                var serverResults = ParseServerToolResults(stepRes.ServerToolResults);
                messages.Add(stepRes.Assistant);

                if (!TryParseToolCalls(stepRes.Assistant, out var textReply, out var toolCalls))
                {
                    _entries.Add(new AgentChatEntry("error", "Assistant returned malformed JSON."));
                    break;
                }

                if (toolCalls.Count == 0)
                {
                    lastSummary = AppendLedger(textReply ?? string.Empty, ledger);
                    _entries.Add(new AgentChatEntry("assistant", lastSummary));
                    break;
                }

                var finished = false;
                foreach (var call in toolCalls)
                {
                    _entries.Add(new AgentChatEntry("tool", $"{call.Name}({call.ArgsJson})"));
                    NotifyChanged();

                    if (serverResults.TryGetValue(call.Id, out var serverMsg))
                    {
                        messages.Add(serverMsg);
                        continue;
                    }

                    var (resJson, isFinish, summary, updatedSnap) = await toolDispatcher.ExecuteAsync(
                        call.Name, call.ArgsJson, snapshotJson, Mode, ledger, AskPermissionAsync, AskUserAsync,
                        (c, i, o, ci, cost) => { AccumulateRawUsage(run, c, i, o, ci, cost); return Task.CompletedTask; }, ct);
                    snapshotJson = updatedSnap;
                    messages.Add(JsonSerializer.Serialize(new { role = "tool", tool_call_id = call.Id, content = resJson }));

                    if (isFinish)
                    {
                        lastSummary = AppendLedger(summary ?? string.Empty, ledger);
                        _entries.Add(new AgentChatEntry("assistant", lastSummary));
                        finished = true;
                        break;
                    }
                }
                if (finished) break;
                NotifyChanged();
            }
        }
        catch (OperationCanceledException)
        {
            if (run.ChatKey == CurrentChatKey)
                _entries.Add(new AgentChatEntry("info", appText["AGENT_STOPPED"]));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent task execution failed.");
            if (run.ChatKey == CurrentChatKey)
                _entries.Add(new AgentChatEntry("error", ex.Message));
        }
        finally
        {
            IsRunning = false;
            WaitingForUserAnswer = false;
            PendingQuestion = null;
            WaitingForPermission = false;
            PendingPermissionLabel = null;
            PendingPermissionRisk = null;
            _cts?.Dispose();
            _cts = null;
            NotifyChanged();

            if (run.ChatKey == CurrentChatKey)
            {
                var historyJson = JsonSerializer.Serialize(_entries);
                var transcriptJson = JsonSerializer.Serialize(messages);
                var durationMs = (int)(DateTimeOffset.UtcNow - run.StartedAt).TotalMilliseconds;
                await chatStore.SaveChatAndTaskAsync(
                    run.ChatKey, run.TaskSeq, goal, lastSummary, historyJson, transcriptJson,
                    run.InputTokens, run.OutputTokens, run.CachedInputTokens, run.Cost, run.Calls, durationMs);
                await LoadChatsAsync();
            }
        }
    }

    private async Task<PermissionDecision> AskPermissionAsync(string label, string risk, CancellationToken ct)
    {
        PendingPermissionLabel = label;
        PendingPermissionRisk = risk;
        WaitingForPermission = true;
        _permissionTcs = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyChanged();
        using var reg = ct.Register(() => _permissionTcs.TrySetCanceled(ct));
        return await _permissionTcs.Task;
    }

    private async Task<string> AskUserAsync(string question, CancellationToken ct)
    {
        PendingQuestion = question;
        WaitingForUserAnswer = true;
        _userAnswerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _entries.Add(new AgentChatEntry("question", question));
        NotifyChanged();
        using var reg = ct.Register(() => _userAnswerTcs.TrySetCanceled(ct));
        return await _userAnswerTcs.Task;
    }

    private async Task<AgentStepResponseDto?> PostStepAsync(List<string> messages, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/agent/step") { Content = JsonContent.Create(new { messages }) };
        var token = await tokenProvider.GetTokenAsync();
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<AgentStepResponseDto>(cancellationToken: ct);
    }

    private void AccumulateUsage(TaskRunState run, AgentUsageDto? u)
    {
        if (u != null) AccumulateRawUsage(run, u.Calls, u.InputTokens, u.OutputTokens, u.CachedInputTokens, u.Cost);
    }

    private void AccumulateRawUsage(TaskRunState run, int calls, int input, int output, int cached, decimal? cost)
    {
        run.Calls += calls; run.InputTokens += input; run.OutputTokens += output; run.CachedInputTokens += cached;
        if (cost.HasValue) run.Cost += cost.Value;
        TotalCalls += calls; TotalInputTokens += input; TotalOutputTokens += output;
        if (cost.HasValue) TotalCost += cost.Value;
    }

    private void ResetCounters()
    {
        TotalCalls = 0; TotalInputTokens = 0; TotalOutputTokens = 0; TotalCost = 0m;
        WaitingForUserAnswer = false; PendingQuestion = null;
        WaitingForPermission = false; PendingPermissionLabel = null; PendingPermissionRisk = null;
    }

    private static string AppendLedger(string summary, List<string> ledger) =>
        ledger.Count == 0 ? summary : $"{summary}\n\n---\n**Actions executed:** {string.Join(" → ", ledger)}";

    private static bool TryParseToolCalls(string assistantJson, out string? textReply, out List<(string Id, string Name, string ArgsJson)> calls)
    {
        textReply = null;
        calls = [];
        try
        {
            using var doc = JsonDocument.Parse(assistantJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tool_calls", out var tcs) || tcs.ValueKind != JsonValueKind.Array || tcs.GetArrayLength() == 0)
            {
                textReply = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : string.Empty;
                return true;
            }
            foreach (var tc in tcs.EnumerateArray())
            {
                var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                if (!tc.TryGetProperty("function", out var fn)) return false;
                var name = fn.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var args = fn.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String ? aEl.GetString() ?? "{}" : "{}";
                calls.Add((id, name, args));
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static Dictionary<string, string> ParseServerToolResults(List<string>? rawResults)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rawResults == null) return map;
        foreach (var raw in rawResults)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("tool_call_id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrEmpty(id)) map[id] = raw;
                }
            }
            catch (JsonException) { }
        }
        return map;
    }

    private void TryLogPriorMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("role", out var r) && r.GetString() == "assistant" &&
                doc.RootElement.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var fn = tc.GetProperty("function");
                    _entries.Add(new AgentChatEntry("tool", $"[server] {fn.GetProperty("name").GetString()}({fn.GetProperty("arguments").GetString()})"));
                }
            }
        }
        catch (JsonException) { }
    }

    private static string NewChatKey() => "c_" + Guid.NewGuid().ToString("N")[..16];
    private void NotifyChanged() => OnStateChanged?.Invoke();
    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    public sealed record AgentChatEntry(string Kind, string Text);

    private sealed class TaskRunState(string chatKey, int taskSeq, DateTimeOffset startedAt)
    {
        public string ChatKey { get; } = chatKey;
        public int TaskSeq { get; } = taskSeq;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public int Calls { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CachedInputTokens { get; set; }
        public decimal Cost { get; set; }
    }

    private sealed class AgentStatusDto
    {
        public bool UseAgent { get; set; }
        public int MaxTaskSteps { get; set; } = 25;
        public int MaxPriorTurns { get; set; } = 80;
        public int MaxTaskModelCalls { get; set; } = 40;
        public decimal MaxTaskCostUsd { get; set; } = 0.80m;
        public bool ShowCost { get; set; } = true;
    }

    private sealed class AgentStepResponseDto
    {
        public string? Assistant { get; set; }
        public List<string>? Prior { get; set; }
        public List<string>? ServerToolResults { get; set; }
        public AgentUsageDto? Usage { get; set; }
        public string? Error { get; set; }
        public decimal? MaxTaskCostUsd { get; set; }
        public int? MaxTaskModelCalls { get; set; }
    }

    private sealed class AgentUsageDto
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CachedInputTokens { get; set; }
        public int Calls { get; set; }
        public decimal? Cost { get; set; }
    }
}
