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
    public event Action? OnLogoutCleared;
    public bool SuppressStateSave { get; private set; }

    private bool _isDrawerOpen;
    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        set
        {
            if (_isDrawerOpen == value) return;
            _isDrawerOpen = value;
            NotifyChanged();
        }
    }

    private AgentMode _mode = AgentMode.Manual;
    public AgentMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            NotifyChanged();
        }
    }
    public bool IsRunning { get; private set; }
    public bool WaitingForUserAnswer { get; private set; }
    public string? PendingQuestion { get; private set; }
    public string[]? PendingQuestionOptions { get; private set; }
    public bool WaitingForPermission { get; private set; }
    public string? PendingPermissionLabel { get; private set; }
    public string? PendingPermissionRisk { get; private set; }

    public IReadOnlyList<AgentChatEntry> Entries => _entries;
    public const long MaxAttachmentBytes = 10 * 1024 * 1024;
    private readonly List<AttachedFileItem> _attachedFiles = [];
    public IReadOnlyList<AttachedFileItem> AttachedFiles => _attachedFiles;
    public bool IsUploadingAttachment { get; private set; }
    public List<AgentChatSummaryDto> SavedChats { get; private set; } = [];
    public string CurrentChatKey { get; private set; } = NewChatKey();
    public int TotalCalls { get; private set; }
    public int TotalInputTokens { get; private set; }
    public int TotalOutputTokens { get; private set; }
    public decimal TotalCost { get; private set; }
    public bool ShowCost { get; private set; } = true;
    public bool AttachmentsEnabled { get; private set; } = true;
    private IReadOnlyList<string> _lastStepTimings = [];
    public IReadOnlyList<string> LastStepTimings => _lastStepTimings;
    public bool HasAlwaysAllowed => permissionGate.HasAlwaysAllowed;
    public void ClearAlwaysAllowed()
    {
        permissionGate.ClearAlwaysAllowed();
        NotifyChanged();
    }
    public int MaxTaskSteps { get; private set; } = 25;
    public int MaxPriorTurns { get; private set; } = 80;
    public decimal MaxTaskCostUsd { get; private set; } = 0.80m;
    public int MaxTaskModelCalls { get; private set; } = 40;

    private readonly AgentStepApiClient _stepApi = new(httpClient, tokenProvider);
    private readonly List<AgentChatEntry> _entries = [];
    private readonly List<string> _lastLedger = [];
    private readonly List<string> _openedLedger = [];
    private readonly List<AgentTaskOutcome> _recentOutcomes = [];
    public IReadOnlyList<string> LastLedger => _lastLedger;
    public IReadOnlyList<string> OpenedLedger => _openedLedger;
    private TaskCompletionSource<string>? _userAnswerTcs;
    private TaskCompletionSource<PermissionDecision>? _permissionTcs;
    private CancellationTokenSource? _cts;
    private Task? _activeRunTask;
    private int _nextTaskSeq = 1;

    public async Task<bool> IsEnabledAsync()
    {
        try
        {
            var res = await _stepApi.GetStatusAsync();
            if (res == null) return false;
            if (res.MaxTaskSteps > 0) MaxTaskSteps = res.MaxTaskSteps;
            if (res.MaxPriorTurns > 0) MaxPriorTurns = res.MaxPriorTurns;
            if (res.MaxTaskModelCalls > 0) MaxTaskModelCalls = res.MaxTaskModelCalls;
            if (res.MaxTaskCostUsd > 0) MaxTaskCostUsd = res.MaxTaskCostUsd;
            ShowCost = res.ShowCost;
            AttachmentsEnabled = res.AttachmentsEnabled;
            toolDispatcher.IsVisionEnabled = res.Vision;
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
        _lastLedger.Clear();
        _openedLedger.Clear();
        _attachedFiles.Clear(); // prevent leaking attachments across chats
        _entries.AddRange(AgentClientTranscriptHelper.DeserializeHistory(detail.HistoryJson, chatKey, logger));
        _recentOutcomes.Clear();
        _recentOutcomes.AddRange(AgentTaskOutcomeMemory.Parse(detail.LogJson));
        permissionGate.ResetSession();
        ResetCounters();
        TotalCalls = detail.TotalCalls;
        TotalInputTokens = detail.TotalInputTokens;
        TotalOutputTokens = detail.TotalOutputTokens;
        TotalCost = detail.TotalCost;
        _lastStepTimings = [];
        NotifyChanged();
        await RestoreChatAttachmentsAsync(detail.ChatKey);
    }

    private async Task RestoreChatAttachmentsAsync(string chatKey)
    {
        if (!AttachmentsEnabled) return;
        try
        {
            var items = await _stepApi.ListChatAttachmentsAsync(chatKey, CancellationToken.None);
            if (items == null)
            {
                logger.LogDebug("Attachment list request for chat {ChatKey} returned a non-success status.", chatKey);
                return;
            }
            if (items.Count == 0 || chatKey != CurrentChatKey) return;
            _attachedFiles.AddRange(items.Where(i => _attachedFiles.All(f => f.Id != i.Id)));
            NotifyChanged();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Restoring attachments of chat {ChatKey} failed.", chatKey);
        }
    }

    public async Task DeleteChatAsync(string chatKey)
    {
        if (!await chatStore.DeleteAsync(chatKey)) return;
        SavedChats.RemoveAll(c => c.ChatKey == chatKey);
        if (CurrentChatKey == chatKey) await NewChatAsync();
        else NotifyChanged();
    }

    public async Task NewChatAsync()
    {
        var oldKey = CurrentChatKey;
        await StopAndWaitAsync();
        CurrentChatKey = NewChatKey();
        _nextTaskSeq = 1;
        _entries.Clear();
        _lastLedger.Clear();
        _openedLedger.Clear();
        _recentOutcomes.Clear();
        _attachedFiles.Clear();
        permissionGate.ResetSession();
        ResetCounters();
        NotifyChanged();
        try { await _stepApi.DeleteChatAttachmentsAsync(oldKey, CancellationToken.None); }
        catch (Exception ex) { logger.LogDebug(ex, "Cleanup attachments failed."); }
    }

    public void AddAttachmentError(string fileName, long sizeBytes, string error)
    {
        _attachedFiles.Add(new AttachedFileItem(Guid.NewGuid().ToString("N"), fileName, "Error", sizeBytes, DateTime.UtcNow, error));
        NotifyChanged();
    }

    public async Task UploadAttachmentAsync(string fileName, byte[] bytes, CancellationToken ct = default)
    {
        if (bytes.Length == 0 || bytes.Length > MaxAttachmentBytes)
        {
            AddAttachmentError(fileName, bytes.LongLength, appText["AGENT_FILE_TOO_BIG"]);
            return;
        }
        var uploadChatKey = CurrentChatKey;
        IsUploadingAttachment = true;
        NotifyChanged();
        try
        {
            var res = await _stepApi.UploadAttachmentAsync(uploadChatKey, fileName, bytes, ct);
            if (uploadChatKey != CurrentChatKey) return;
            if (res == null || !string.IsNullOrWhiteSpace(res.Error) || string.IsNullOrWhiteSpace(res.Id))
                AddAttachmentError(fileName, bytes.LongLength, res?.Error ?? appText["AGENT_FILE_UPLOAD_ERR"]);
            else
                _attachedFiles.Add(new AttachedFileItem(res.Id, res.FileName ?? fileName, res.Kind ?? "File", res.SizeBytes, res.ExpiresUtc));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Attachment upload failed.");
            if (uploadChatKey == CurrentChatKey)
                AddAttachmentError(fileName, bytes.LongLength, appText["AGENT_FILE_SEND_ERR"]);
        }
        finally
        {
            IsUploadingAttachment = false;
            NotifyChanged();
        }
    }

    public async Task RemoveAttachmentAsync(string id)
    {
        var item = _attachedFiles.FirstOrDefault(f => f.Id == id);
        if (item == null) return;
        _attachedFiles.Remove(item);
        NotifyChanged();
        if (item.Error == null)
        {
            try { await _stepApi.DeleteAttachmentAsync(id, CancellationToken.None); }
            catch (Exception ex) { logger.LogDebug(ex, "Delete attachment failed."); }
        }
    }

    public async Task ClearOnLogoutAsync()
    {
        SuppressStateSave = true;
        try
        {
            CurrentChatKey = NewChatKey();
            IsDrawerOpen = false;
            await StopAndWaitAsync();
            _nextTaskSeq = 1;
            _entries.Clear();
            _lastLedger.Clear();
            _openedLedger.Clear();
            _recentOutcomes.Clear();
            _attachedFiles.Clear(); // clear attachments on logout
            SavedChats = [];
            permissionGate.ResetSession();
            ResetCounters();
            Mode = AgentMode.Manual;
            OnLogoutCleared?.Invoke();
            NotifyChanged();
        }
        finally
        {
            await toolDispatcher.ClearPanelStateAsync();
            SuppressStateSave = false;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _userAnswerTcs?.TrySetCanceled();
        _permissionTcs?.TrySetCanceled();
        ResetPromptState();
        NotifyChanged();
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
        if (!WaitingForUserAnswer || _userAnswerTcs == null) return;
        WaitingForUserAnswer = false;
        PendingQuestion = null;
        PendingQuestionOptions = null;
        _entries.Add(new AgentChatEntry("user", answer, SubType: AgentChatEntry.SubTypeAnswer));
        NotifyChanged();
        _userAnswerTcs.TrySetResult(answer);
    }

    public void ResolvePermission(PermissionDecision decision)
    {
        if (!WaitingForPermission || _permissionTcs == null) return;
        WaitingForPermission = false;
        PendingPermissionLabel = null;
        PendingPermissionRisk = null;
        NotifyChanged();
        _permissionTcs.TrySetResult(decision);
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
        _entries.Add(new AgentChatEntry("user", goal, SubType: AgentChatEntry.SubTypeTask));
        NotifyChanged();

        var messages = new List<string>();
        var lastSummary = string.Empty;
        var tracker = new AgentTaskRepeatTracker();
        _lastStepTimings = [];
        try
        {
            var isDeleteGoal = AgentLoopGuards.IsDeleteTaskGoal(goal);
            var adviceOnlyGoal = AgentLoopGuards.IsReadOnlyAdviceGoal(goal);
            var activeFiles = AttachedFiles.Where(f => f.Error == null && f.ExpiresUtc > DateTime.UtcNow).ToList();
            toolDispatcher.ResetSafety(isDeleteGoal, activeFiles.Count > 0, adviceOnlyGoal);
            toolDispatcher.SetUserEvidence(goal);
            var snapshotJson = await toolDispatcher.CaptureSnapshotAsync(ct);
            tracker.NoteFingerprint(AgentTaskRepeatTracker.ExtractFingerprint(snapshotJson));
            var goalPrefix = isDeleteGoal ? "[Delete task: yes]\n" : string.Empty;
            // The outcomes block follows the goal line, so server guards that read the first line are unaffected.
            var outcomeBlock = AgentTaskOutcomeMemory.Block(_recentOutcomes);
            var goalText = outcomeBlock.Length == 0 ? goalPrefix + goal : goalPrefix + goal + "\n\n" + outcomeBlock;
            var taskPrompt = activeFiles.Count == 0
                ? goalText
                : string.Join("\n", activeFiles.Select(f => $"[att:{f.Id} \"{f.FileName}\", {f.Kind}]")) + "\n" + goalText;
            messages = AgentClientTranscriptHelper.BuildInitialMessages(priorTurns, taskPrompt, snapshotJson);
            lastSummary = await ExecuteStepLoopAsync(run, messages, ledger, snapshotJson, tracker, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Close(AgentTaskOutcomeMemory.Stopped);
            if (run.ChatKey == CurrentChatKey) _entries.Add(new AgentChatEntry("info", appText["AGENT_STOPPED"]));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent task execution failed.");
            run.Close(AgentTaskOutcomeMemory.Error, "task failed");
            if (run.ChatKey == CurrentChatKey) _entries.Add(new AgentChatEntry("error", ex.Message));
        }
        finally
        {
            // A task that cleared grid filters must leave them as the user had them, on every exit path.
            if (tracker.FiltersCleared)
                await toolDispatcher.RestoreFiltersAfterTaskAsync(CancellationToken.None);
            await PersistCompletedTaskAsync(run, goal, lastSummary, messages);
        }
    }

    private async Task<string> ExecuteStepLoopAsync(
        TaskRunState run, List<string> messages, List<string> ledger, string snapshotJson,
        AgentTaskRepeatTracker tracker, CancellationToken ct)
    {
        var emptyRetries = 0;
        var finishRetries = 0;
        var injectedFinalCall = false;
        var recentSignatures = new List<string>();
        _lastLedger.Clear();
        _openedLedger.Clear();
        for (var step = 0; step < MaxTaskSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            if (run.Calls >= MaxTaskModelCalls || run.Cost >= MaxTaskCostUsd)
            {
                _entries.Add(new AgentChatEntry("info", $"Task budget reached (calls: {run.Calls}/{MaxTaskModelCalls}, cost: ${run.Cost:0.0000}/${MaxTaskCostUsd:0.00})."));
                run.Close(AgentTaskOutcomeMemory.Blocked, "budget reached");
                return string.Empty;
            }

            if (!injectedFinalCall && AgentLoopGuards.ShouldInjectPredictiveFinalCall(
                    step, MaxTaskSteps, run.Calls, MaxTaskModelCalls, run.Cost, MaxTaskCostUsd))
            {
                injectedFinalCall = true;
                messages.Add(JsonSerializer.Serialize(new
                {
                    role = "user",
                    content = "[final call] Step or budget limit approaching. Summarize what you completed or found so far cleanly without extra tool loops."
                }));
            }

            var stepRes = await _stepApi.PostStepAsync(messages, run, ct);
            if (stepRes == null)
            {
                _entries.Add(new AgentChatEntry("error", "Empty response from agent server."));
                run.Close(AgentTaskOutcomeMemory.Error, "empty response");
                return string.Empty;
            }

            if (stepRes.MaxTaskCostUsd is > 0) MaxTaskCostUsd = stepRes.MaxTaskCostUsd.Value;
            if (stepRes.MaxTaskModelCalls is > 0) MaxTaskModelCalls = stepRes.MaxTaskModelCalls.Value;
            AccumulateUsage(run, stepRes.Usage);
            _lastStepTimings = stepRes.Timings ?? [];

            if (!string.IsNullOrWhiteSpace(stepRes.Error))
            {
                _entries.Add(new AgentChatEntry("error", stepRes.Error));
                run.Close(AgentTaskOutcomeMemory.Error, "agent error");
                return string.Empty;
            }
            if (string.IsNullOrWhiteSpace(stepRes.Assistant))
            {
                _entries.Add(new AgentChatEntry("error", "Assistant returned an empty message."));
                run.Close(AgentTaskOutcomeMemory.Error, "empty message");
                return string.Empty;
            }

            foreach (var p in stepRes.Prior ?? [])
            {
                messages.Add(p);
                AgentClientTranscriptHelper.TryLogPriorMessage(p, _entries, logger);
                TryLogHelpContextNote(p);
            }
            messages.Add(stepRes.Assistant);

            if (!AgentClientTranscriptHelper.TryParseToolCalls(stepRes.Assistant, out var textReply, out var toolCalls))
            {
                _entries.Add(new AgentChatEntry("error", "Assistant returned malformed JSON."));
                run.Close(AgentTaskOutcomeMemory.Error, "malformed JSON");
                return string.Empty;
            }

            var unsavedAfterRetries = false;
            if (toolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(textReply))
                {
                    // Remove empty assistant turn before retry/fallback so providers do not reject empty assistant messages.
                    messages.RemoveAt(messages.Count - 1);
                    if (emptyRetries < 2)
                    {
                        emptyRetries++;
                        messages.Add(AgentClientTranscriptHelper.SerializeEmptyReplyRetry());
                        continue;
                    }
                }
                else
                {
                    var unsavedErr = toolDispatcher.CheckFinishAllowed();
                    if (unsavedErr != null && finishRetries < 2)
                    {
                        finishRetries++;
                        messages.Add(AgentClientTranscriptHelper.SerializeFinishBlockedNudge(unsavedErr));
                        continue;
                    }
                    unsavedAfterRetries = unsavedErr != null;
                }
                var summaryText = string.IsNullOrWhiteSpace(textReply) ? appText["AGENT_EMPTY_REPLY"] : textReply;
                if (string.IsNullOrWhiteSpace(textReply)) run.Close(AgentTaskOutcomeMemory.Blocked, "empty reply");
                else if (unsavedAfterRetries) run.Close(AgentTaskOutcomeMemory.Blocked, "unsaved changes");
                else run.Close(AgentTaskOutcomeMemory.Done);
                var summary = AgentClientTranscriptHelper.AppendLedger(summaryText, ledger, appText["AGENT_ACTIONS_DONE"]);
                _entries.Add(new AgentChatEntry("assistant", summary));
                return summary;
            }

            emptyRetries = 0;
            var serverResults = AgentClientTranscriptHelper.ParseServerToolResults(stepRes.ServerToolResults, logger);
            var (finished, finalSummary, updatedSnap) = await ExecuteToolBatchAsync(toolCalls, serverResults, run, messages, ledger, recentSignatures, snapshotJson, tracker, step < MaxTaskSteps - 1 && !injectedFinalCall, ct);
            snapshotJson = updatedSnap;
            if (finished) return finalSummary;

            var loopWarning = AgentLoopGuards.DetectLoop(recentSignatures);
            if (loopWarning != null)
            {
                messages.Add(JsonSerializer.Serialize(new { role = "user", content = loopWarning }));
            }
            if (tracker.LoopBlocks >= AgentTaskRepeatTracker.MaxLoopBlocks && !injectedFinalCall)
            {
                injectedFinalCall = true;
                messages.Add(JsonSerializer.Serialize(new
                {
                    role = "user",
                    content = "[final call] The loop guard blocked repeated actions twice. Summarize what you have done or found and finish; do not repeat the blocked actions."
                }));
            }
            NotifyChanged();
        }
        run.Close(AgentTaskOutcomeMemory.Blocked, "step limit reached");
        return string.Empty;
    }

    private void TryLogHelpContextNote(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("role", out var r) || r.GetString() != "user") return;
            var content = doc.RootElement.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(content) &&
                (content.Contains("Relevant CRM Guide Context", StringComparison.Ordinal) ||
                 content.Contains("Knowledge notes", StringComparison.Ordinal)))
            {
                _entries.Add(new AgentChatEntry("help", content));
            }
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Skipped malformed prior context note JSON.");
        }
    }

    private async Task<(bool Finished, string Summary, string SnapshotJson)> ExecuteToolBatchAsync(
        List<AgentToolCall> toolCalls, Dictionary<string, string> serverResults, TaskRunState run,
        List<string> messages, List<string> ledger, List<string> recentSignatures, string snapshotJson,
        AgentTaskRepeatTracker tracker, bool allowLearningNudge, CancellationToken ct)
    {
        foreach (var call in toolCalls)
        {
            var toolEntryIndex = _entries.Count;
            _entries.Add(new AgentChatEntry("tool", $"{call.Name}({call.ArgsJson})"));
            recentSignatures.Add($"{call.Name}:{call.ArgsJson.Trim()}");
            NotifyChanged();

            if (serverResults.TryGetValue(call.Id, out var serverMsg))
            {
                tracker.MarkAction(call.Name); // server tool ran (even if policy-blocked)
                if (call.Name == "read_data") toolDispatcher.MarkRecordRead();
                if (serverMsg.Contains("\"blocked\":true", StringComparison.Ordinal))
                    _entries[toolEntryIndex] = _entries[toolEntryIndex] with { IsBlocked = true };
                messages.Add(serverMsg);
                continue;
            }

            // finish/ask_user must never be refused; every other UI tool passes the repeat tracker.
            if (call.Name is not ("finish" or "ask_user"))
            {
                var repeatBlock = tracker.PreBlockReason(call.Name, call.ArgsJson);
                if (repeatBlock != null)
                {
                    tracker.MarkLoopBlock();
                    _entries[toolEntryIndex] = _entries[toolEntryIndex] with { IsBlocked = true };
                    messages.Add(AgentClientTranscriptHelper.SerializeToolResult(
                        call.Id, JsonSerializer.Serialize(new { ok = false, blocked = true, reason = repeatBlock })));
                    continue;
                }
            }

            var (resJson, isFinish, summary, updatedSnap) = await toolDispatcher.ExecuteAsync(
                call.Name, call.ArgsJson, snapshotJson, Mode, ledger, AskPermissionAsync, AskUserAsync,
                (c, i, o, ci, cost) => { AccumulateRawUsage(run, c, i, o, ci, cost); return Task.CompletedTask; }, ct);
            snapshotJson = updatedSnap;
            tracker.Record(call.Name, call.ArgsJson, AgentTaskRepeatTracker.ExtractFingerprint(resJson));
            if (call.Name == "clear_filters" && resJson.Contains("\"canRestore\":true", StringComparison.Ordinal))
                tracker.FiltersCleared = true;
            if (resJson.Contains("\"blocked\":true", StringComparison.Ordinal) || resJson.Contains("\"declined\":true", StringComparison.Ordinal))
                _entries[toolEntryIndex] = _entries[toolEntryIndex] with { IsBlocked = true };
            if (call.Name is "go_to_page" or "select_tab" && resJson.Contains("\"ok\":true", StringComparison.Ordinal))
                _openedLedger.Add($"{call.Name}: {call.ArgsJson}");
            _lastLedger.Clear();
            _lastLedger.AddRange(ledger.Distinct(StringComparer.Ordinal));
            messages.Add(AgentClientTranscriptHelper.SerializeToolResult(call.Id, resJson));

            if (isFinish && allowLearningNudge && tracker.ShouldNudgeLearning())
            {
                // One-shot learning nudge: a long task that never touched remember/correct_fact
                // gets one chance to record a lesson; the next finish is accepted unconditionally.
                // Never fires when the loop guard already forced the finish or on the last step /
                // after "[final call]" (there is no spare step to act on it -> the Done would be lost).
                messages.Add(JsonSerializer.Serialize(new
                {
                    role = "user",
                    content = "[learning nudge] This task took many steps. If the user taught a reusable CRM rule or you learned a convention worth keeping and the `remember` / `correct_fact` tools are available, call one now. If they are not available or nothing is worth learning, call `finish` again with the same summary."
                }));
                break;
            }
            if (isFinish)
            {
                run.Close(AgentTaskOutcomeMemory.Done);
                var finalSummary = AgentClientTranscriptHelper.AppendLedger(summary ?? string.Empty, ledger, appText["AGENT_ACTIONS_DONE"]);
                _entries.Add(new AgentChatEntry("assistant", finalSummary));
                return (true, finalSummary, snapshotJson);
            }
        }
        return (false, string.Empty, snapshotJson);
    }

    private async Task PersistCompletedTaskAsync(TaskRunState run, string goal, string lastSummary, List<string> messages)
    {
        IsRunning = false;
        ResetPromptState();
        _cts?.Dispose();
        _cts = null;
        NotifyChanged();

        if (run.ChatKey != CurrentChatKey) return;
        AgentTaskOutcomeMemory.Add(_recentOutcomes, goal, run.Outcome, run.OutcomeReason);
        try
        {
            AgentClientTranscriptHelper.BlankAttachmentToolResults(messages, logger);
            var durationMs = (int)(DateTimeOffset.UtcNow - run.StartedAt).TotalMilliseconds;
            await chatStore.SaveChatAndTaskAsync(
                run.ChatKey, run.TaskSeq, goal, lastSummary,
                JsonSerializer.Serialize(_entries), JsonSerializer.Serialize(messages),
                run.InputTokens, run.OutputTokens, run.CachedInputTokens, run.Cost, run.Calls, durationMs,
                AgentTaskOutcomeMemory.Serialize(_recentOutcomes));
            await LoadChatsAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist completed agent task {ChatKey}.", run.ChatKey);
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

    private async Task<string> AskUserAsync(string question, string[]? options, CancellationToken ct)
    {
        PendingQuestion = question;
        PendingQuestionOptions = options is { Length: > 0 } ? options : null;
        WaitingForUserAnswer = true;
        _userAnswerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _entries.Add(new AgentChatEntry("question", question));
        NotifyChanged();
        using var reg = ct.Register(() => _userAnswerTcs.TrySetCanceled(ct));
        var answer = await _userAnswerTcs.Task;
        toolDispatcher.AppendUserEvidence(answer);
        return answer;
    }

    private void AccumulateUsage(TaskRunState run, AgentUsageDto? u)
    {
        if (u != null) AccumulateRawUsage(run, u.Calls, u.InputTokens, u.OutputTokens, u.CachedInputTokens, u.Cost);
    }

    private void AccumulateRawUsage(TaskRunState run, int calls, int input, int output, int cached, decimal? cost)
    {
        run.Calls += calls; run.InputTokens += input; run.OutputTokens += output; run.CachedInputTokens += cached;
        TotalCalls += calls; TotalInputTokens += input; TotalOutputTokens += output;
        if (cost.HasValue) { run.Cost += cost.Value; TotalCost += cost.Value; }
    }

    private void ResetCounters()
    {
        TotalCalls = 0; TotalInputTokens = 0; TotalOutputTokens = 0; TotalCost = 0m;
        _lastStepTimings = [];
        ResetPromptState();
    }

    private void ResetPromptState()
    {
        WaitingForUserAnswer = false; PendingQuestion = null; PendingQuestionOptions = null;
        WaitingForPermission = false; PendingPermissionLabel = null; PendingPermissionRisk = null;
    }

    private static string NewChatKey() => "c_" + Guid.NewGuid().ToString("N")[..16];
    private void NotifyChanged() => OnStateChanged?.Invoke();
    public void Dispose() { Stop(); _cts?.Dispose(); }
    public sealed record AgentChatEntry(string Kind, string Text, bool IsBlocked = false, string? SubType = null)
    {
        /// <summary>SubType values distinguishing a task goal from a mid-task user answer.</summary>
        public const string SubTypeTask = "task";
        public const string SubTypeAnswer = "answer";
    }
}
