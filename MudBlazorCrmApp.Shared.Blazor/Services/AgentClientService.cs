using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazorCrmApp.Shared.Blazor.Authorization;

namespace MudBlazorCrmApp.Shared.Blazor.Services;

public enum AgentMode
{
    Manual,
    SemiAuto,
    Auto
}

public sealed class AgentClientService(
    HttpClient httpClient,
    IJSRuntime jsRuntime,
    NavigationManager navigationManager,
    AuthenticationStateProvider authenticationStateProvider,
    AppText appText)
{
    private const int MaxSteps = 25;

    private static readonly Dictionary<string, string> PageRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard"] = "/crmdashboard",
        ["داشبورد"] = "/crmdashboard",
        ["crmdashboard"] = "/crmdashboard",
        ["pipeline"] = "/pipeline",
        ["پایپ‌لاین"] = "/pipeline",
        ["بورد پایپ‌لاین"] = "/pipeline",
        ["calendar"] = "/calendar",
        ["تقویم"] = "/calendar",
        ["leads"] = "/Lead",
        ["lead"] = "/Lead",
        ["سرنخ‌ها"] = "/Lead",
        ["سرنخ"] = "/Lead",
        ["opportunities"] = "/Opportunity",
        ["opportunity"] = "/Opportunity",
        ["فرصت‌ها"] = "/Opportunity",
        ["فرصت"] = "/Opportunity",
        ["sales"] = "/Sale",
        ["sale"] = "/Sale",
        ["فروش"] = "/Sale",
        ["فروش‌ها"] = "/Sale",
        ["activities"] = "/Activity",
        ["activity"] = "/Activity",
        ["فعالیت‌ها"] = "/Activity",
        ["فعالیت"] = "/Activity",
        ["customers"] = "/Customer",
        ["customer"] = "/Customer",
        ["companies"] = "/Customer",
        ["مشتریان"] = "/Customer",
        ["شرکت‌ها"] = "/Customer",
        ["contacts"] = "/Contact",
        ["contact"] = "/Contact",
        ["مخاطبین"] = "/Contact",
        ["addresses"] = "/Address",
        ["address"] = "/Address",
        ["آدرس‌ها"] = "/Address",
        ["products"] = "/Product",
        ["product"] = "/Product",
        ["محصولات"] = "/Product",
        ["services"] = "/Service",
        ["service"] = "/Service",
        ["خدمات"] = "/Service",
        ["vendors"] = "/Vendor",
        ["vendor"] = "/Vendor",
        ["تامین‌کنندگان"] = "/Vendor",
        ["support"] = "/SupportCase",
        ["support cases"] = "/SupportCase",
        ["supportcase"] = "/SupportCase",
        ["تیکت‌ها"] = "/SupportCase",
        ["تیکت‌های پشتیبانی"] = "/SupportCase",
        ["communications"] = "/communication",
        ["ارتباطات"] = "/communication",
        ["tasks"] = "/TodoTask",
        ["todotask"] = "/TodoTask",
        ["وظایف"] = "/TodoTask",
        ["لیست وظایف"] = "/TodoTask",
        ["kanban"] = "/KanbanTodoTask",
        ["کانبان"] = "/KanbanTodoTask",
        ["rewards"] = "/Reward",
        ["پاداش‌ها"] = "/Reward",
        ["tags"] = "/Tag",
        ["برچسب‌ها"] = "/Tag",
        ["reports"] = "/reports",
        ["گزارش‌ها"] = "/reports",
        ["import"] = "/import",
        ["email templates"] = "/emailtemplate",
        ["users"] = "/user",
        ["کاربران"] = "/user",
        ["audit log"] = "/auditlog"
    };

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

    public List<AgentChatEntry> Entries { get; } = [];
    public List<AgentChatSummaryDto> SavedChats { get; private set; } = [];
    public string CurrentChatKey { get; private set; } = NewChatKey();
    public int TotalCalls { get; private set; }
    public int TotalInputTokens { get; private set; }
    public int TotalOutputTokens { get; private set; }
    public decimal TotalCost { get; private set; }

    private TaskCompletionSource<string>? _userAnswerTcs;
    private TaskCompletionSource<PermissionDecision>? _permissionTcs;
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _alwaysAllowedActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _declinedActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ledger = [];
    private string _latestSnapshotJson = "{}";

    public async Task<bool> IsEnabledAsync()
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<AgentStatusDto>("/api/agent/status");
            return res?.UseAgent ?? false;
        }
        catch
        {
            return false;
        }
    }

    public void NewChat()
    {
        Stop();
        CurrentChatKey = NewChatKey();
        Entries.Clear();
        TotalCalls = 0;
        TotalInputTokens = 0;
        TotalOutputTokens = 0;
        TotalCost = 0m;
        WaitingForUserAnswer = false;
        PendingQuestion = null;
        WaitingForPermission = false;
        PendingPermissionLabel = null;
        PendingPermissionRisk = null;
        _alwaysAllowedActions.Clear();
        _declinedActions.Clear();
        _ledger.Clear();
        NotifyChanged();
    }

    private static string NewChatKey() => "c_" + Guid.NewGuid().ToString("N")[..16];

    public async Task LoadChatsAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/agentchat");
            await AttachBearerIfPresentAsync(req);
            using var res = await httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return;
            var payload = await res.Content.ReadFromJsonAsync<AgentChatListDto>();
            if (payload?.Chats != null)
            {
                SavedChats = payload.Chats;
                NotifyChanged();
            }
        }
        catch { }
    }

    public async Task LoadChatAsync(string chatKey)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/agentchat/{chatKey}");
            await AttachBearerIfPresentAsync(req);
            using var res = await httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return;
            var detail = await res.Content.ReadFromJsonAsync<AgentChatDetailDto>();
            if (detail == null || string.IsNullOrEmpty(detail.ChatKey)) return;

            Stop();
            CurrentChatKey = detail.ChatKey;
            Entries.Clear();
            if (!string.IsNullOrWhiteSpace(detail.HistoryJson))
            {
                var loaded = JsonSerializer.Deserialize<List<AgentChatEntry>>(detail.HistoryJson);
                if (loaded != null) Entries.AddRange(loaded);
            }
            TotalCalls = 0;
            TotalInputTokens = 0;
            TotalOutputTokens = 0;
            TotalCost = 0m;
            NotifyChanged();
        }
        catch { }
    }

    public async Task DeleteChatAsync(string chatKey)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/agentchat/{chatKey}");
            await AttachBearerIfPresentAsync(req);
            await httpClient.SendAsync(req);
            SavedChats.RemoveAll(c => c.ChatKey == chatKey);
            if (CurrentChatKey == chatKey) NewChat();
            else NotifyChanged();
        }
        catch { }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _userAnswerTcs?.TrySetCanceled();
        _permissionTcs?.TrySetCanceled();
    }

    public void SubmitUserAnswer(string answer)
    {
        if (WaitingForUserAnswer && _userAnswerTcs != null)
        {
            WaitingForUserAnswer = false;
            PendingQuestion = null;
            Entries.Add(new AgentChatEntry("user", answer));
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

    public async Task SendTaskAsync(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal) || IsRunning) return;

        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _declinedActions.Clear();
        _ledger.Clear();

        Entries.Add(new AgentChatEntry("user", goal));
        NotifyChanged();

        var taskStart = DateTimeOffset.UtcNow;
        var lastSummary = string.Empty;
        var messages = new List<string>();
        try
        {
            // Include recent conversation history (user/assistant turns) so the agent remembers earlier tasks in this chat
            var priorTurns = Entries
                .Where(e => e.Kind is "user" or "assistant")
                .TakeLast(10)
                .ToList();
            foreach (var turn in priorTurns)
            {
                messages.Add(JsonSerializer.Serialize(new
                {
                    role = turn.Kind,
                    content = turn.Text
                }));
            }

            _latestSnapshotJson = await CaptureSnapshotAsync();
            messages.Add(
                JsonSerializer.Serialize(new
                {
                    role = "user",
                    content = $"Goal: {goal}\nDate: {DateTime.Now:yyyy-MM-dd}\nCurrent screen snapshot:\n{_latestSnapshotJson}"
                })
            );

            for (var step = 0; step < MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();

                var stepRes = await PostStepAsync(messages, ct);
                if (stepRes == null)
                {
                    Entries.Add(new AgentChatEntry("error", "Empty response from agent server."));
                    break;
                }

                AccumulateUsage(stepRes.Usage);

                if (!string.IsNullOrWhiteSpace(stepRes.Error))
                {
                    Entries.Add(new AgentChatEntry("error", stepRes.Error));
                    break;
                }

                if (string.IsNullOrWhiteSpace(stepRes.Assistant))
                {
                    Entries.Add(new AgentChatEntry("error", "Assistant returned an empty message."));
                    break;
                }

                if (stepRes.Prior is { Count: > 0 })
                {
                    foreach (var priorMsg in stepRes.Prior)
                    {
                        messages.Add(priorMsg);
                        TryLogPriorMessage(priorMsg);
                    }
                }

                var serverResultsById = ParseServerToolResults(stepRes.ServerToolResults);
                messages.Add(stepRes.Assistant);

                using var assistantDoc = JsonDocument.Parse(stepRes.Assistant);
                var msgEl = assistantDoc.RootElement;

                if (!msgEl.TryGetProperty("tool_calls", out var toolCallsEl) ||
                    toolCallsEl.ValueKind != JsonValueKind.Array ||
                    toolCallsEl.GetArrayLength() == 0)
                {
                    var text = msgEl.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString()
                        : string.Empty;
                    Entries.Add(new AgentChatEntry("assistant", AppendLedger(text ?? string.Empty)));
                    break;
                }

                var finished = false;
                foreach (var callEl in toolCallsEl.EnumerateArray())
                {
                    var callId = callEl.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                    var fn = callEl.GetProperty("function");
                    var name = fn.GetProperty("name").GetString() ?? string.Empty;
                    var argsJson = fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String
                        ? a.GetString() ?? "{}"
                        : "{}";

                    Entries.Add(new AgentChatEntry("tool", $"{name}({argsJson})"));
                    NotifyChanged();

                    if (serverResultsById.TryGetValue(callId, out var serverToolMsg))
                    {
                        messages.Add(serverToolMsg);
                        continue;
                    }

                    var (toolResultJson, isFinish, finishSummary) = await ExecuteUiToolAsync(name, argsJson, ct);
                    messages.Add(JsonSerializer.Serialize(new
                    {
                        role = "tool",
                        tool_call_id = callId,
                        content = toolResultJson
                    }));

                    if (isFinish)
                    {
                        lastSummary = AppendLedger(finishSummary ?? string.Empty);
                        Entries.Add(new AgentChatEntry("assistant", lastSummary));
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
            Entries.Add(new AgentChatEntry("info", appText["AGENT_STOPPED"]));
        }
        catch (Exception ex)
        {
            Entries.Add(new AgentChatEntry("error", ex.Message));
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
            _ = SaveChatAndTaskAsync(goal, lastSummary, messages, (int)(DateTimeOffset.UtcNow - taskStart).TotalMilliseconds);
        }
    }

    private async Task SaveChatAndTaskAsync(string goal, string summary, List<string> messages, int durationMs)
    {
        try
        {
            var title = goal.Length > 80 ? goal[..80] : goal;
            var historyJson = JsonSerializer.Serialize(Entries);
            using var chatReq = new HttpRequestMessage(HttpMethod.Put, $"/api/agentchat/{CurrentChatKey}")
            {
                Content = JsonContent.Create(new { title, historyJson, logJson = "[]" })
            };
            await AttachBearerIfPresentAsync(chatReq);
            await httpClient.SendAsync(chatReq);

            var taskSeq = (int)Math.Max(1, DateTimeOffset.UtcNow.ToUnixTimeSeconds() % int.MaxValue);
            using var taskReq = new HttpRequestMessage(HttpMethod.Put, $"/api/agentchat/{CurrentChatKey}/tasks/{taskSeq}")
            {
                Content = JsonContent.Create(new
                {
                    goal,
                    summary,
                    transcriptJson = JsonSerializer.Serialize(messages),
                    inputTokens = TotalInputTokens,
                    outputTokens = TotalOutputTokens,
                    cachedInputTokens = 0,
                    cost = TotalCost,
                    modelCalls = TotalCalls,
                    durationMs
                })
            };
            await AttachBearerIfPresentAsync(taskReq);
            await httpClient.SendAsync(taskReq);
            await LoadChatsAsync();
        }
        catch { }
    }

    private async Task<(string ResultJson, bool IsFinish, string? Summary)> ExecuteUiToolAsync(
        string name,
        string argsJson,
        CancellationToken ct)
    {
        using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = argsDoc.RootElement;

        switch (name)
        {
            case "scan_screen":
            {
                await jsRuntime.InvokeVoidAsync("crmAgentDriver.waitForSettle", ct, 800);
                _latestSnapshotJson = await CaptureSnapshotAsync();
                return (JsonSerializer.Serialize(new { ok = true, snapshot = JsonNode.Parse(_latestSnapshotJson) }), false, null);
            }

            case "go_to_page":
            {
                var target = args.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var route = ResolveRoute(target);
                if (route == null)
                {
                    return (JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = $"Unknown page '{target}'.",
                        availableRoutes = PageRoutes.Values.Distinct().OrderBy(x => x).ToArray()
                    }), false, null);
                }

                navigationManager.NavigateTo(route);
                await jsRuntime.InvokeVoidAsync("crmAgentDriver.waitForSettle", ct, 1500);
                _latestSnapshotJson = await CaptureSnapshotAsync();
                return (JsonSerializer.Serialize(new { ok = true, navigatedTo = route, snapshot = JsonNode.Parse(_latestSnapshotJson) }), false, null);
            }

            case "select_tab":
            {
                var id = args.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.selectTab", ct, id);
                _latestSnapshotJson = await CaptureSnapshotAsync();
                return (JsonSerializer.Serialize(new { result = JsonNode.Parse(res), snapshot = JsonNode.Parse(_latestSnapshotJson) }), false, null);
            }

            case "set_field":
            {
                var id = args.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var val = args.TryGetProperty("value", out var vEl) ? vEl.GetString() ?? "" : "";
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.setField", ct, id, val);
                _ledger.Add($"set_field({id} = \"{val}\")");
                _latestSnapshotJson = await CaptureSnapshotAsync();
                return (JsonSerializer.Serialize(new { result = JsonNode.Parse(res), snapshot = JsonNode.Parse(_latestSnapshotJson) }), false, null);
            }

            case "open_combo":
            {
                var id = args.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.openCombo", ct, id);
                return (res, false, null);
            }

            case "pick_option":
            {
                var opt = args.TryGetProperty("option", out var oEl) ? oEl.GetString() ?? "" : "";
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.pickOption", ct, opt);
                _ledger.Add($"pick_option(\"{opt}\")");
                _latestSnapshotJson = await CaptureSnapshotAsync();
                return (JsonSerializer.Serialize(new { result = JsonNode.Parse(res), snapshot = JsonNode.Parse(_latestSnapshotJson) }), false, null);
            }

            case "close_combo":
            {
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.closeCombo", ct);
                return (res, false, null);
            }

            case "click_action":
            {
                var id = args.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var targetName = args.TryGetProperty("targetName", out var tEl) ? tEl.GetString() : null;
                var (actionText, risk) = LookupActionFromSnapshot(id);

                if (_declinedActions.Contains(actionText))
                {
                    return (JsonSerializer.Serialize(new
                    {
                        ok = false,
                        blocked = true,
                        declined = true,
                        reason = $"User previously declined '{actionText}'. Do not retry."
                    }), false, null);
                }

                var needsPermission = risk switch
                {
                    "destructive" => Mode != AgentMode.Auto || string.IsNullOrWhiteSpace(targetName),
                    "save" => Mode != AgentMode.Auto && !_alwaysAllowedActions.Contains(actionText),
                    _ => false
                };

                if (needsPermission)
                {
                    var label = string.IsNullOrWhiteSpace(targetName) ? actionText : $"{actionText}: {targetName}";
                    var decision = await AskPermissionAsync(label, risk, ct);
                    if (decision == PermissionDecision.Deny)
                    {
                        _declinedActions.Add(actionText);
                        return (JsonSerializer.Serialize(new
                        {
                            ok = false,
                            declined = true,
                            reason = $"User declined '{label}'. Do not retry."
                        }), false, null);
                    }
                    if (decision == PermissionDecision.AllowAlways && risk != "destructive")
                    {
                        _alwaysAllowedActions.Add(actionText);
                    }
                }

                var wasInDialog = IsInDialog(_latestSnapshotJson);
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.clickAction", ct, id);
                _ledger.Add($"click_action({actionText} [{risk}])");
                _latestSnapshotJson = await CaptureSnapshotAsync();
                var (stillInDialog, hasErrors) = CheckDialogAndErrors(_latestSnapshotJson);
                var saveDidNotComplete = risk == "save" && ((wasInDialog && stillInDialog) || hasErrors);
                return (JsonSerializer.Serialize(new
                {
                    result = JsonNode.Parse(res),
                    saveDidNotComplete,
                    snapshot = JsonNode.Parse(_latestSnapshotJson)
                }), false, null);
            }

            case "read_grid_headers":
            {
                var find = args.TryGetProperty("find", out var fEl) ? fEl.GetString() : null;
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.readGridHeaders", ct, find);
                return (res, false, null);
            }

            case "select_grid_row":
            {
                var rowId = args.TryGetProperty("rowId", out var rEl) ? rEl.GetString() ?? "" : "";
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.selectGridRow", ct, rowId);
                _latestSnapshotJson = await CaptureSnapshotAsync();
                return (JsonSerializer.Serialize(new { result = JsonNode.Parse(res), snapshot = JsonNode.Parse(_latestSnapshotJson) }), false, null);
            }

            case "look_at_screen":
            {
                var question = args.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
                var visibleText = await jsRuntime.InvokeAsync<string>("crmAgentDriver.captureVisibleText", ct);
                var lookRes = await PostLookAsync(visibleText, question, ct);
                if (lookRes?.Usage != null) AccumulateUsage(lookRes.Usage);

                if (lookRes == null || !string.IsNullOrWhiteSpace(lookRes.Error))
                {
                    return (JsonSerializer.Serialize(new { ok = false, error = lookRes?.Error ?? "Look failed." }), false, null);
                }
                return (JsonSerializer.Serialize(new { ok = true, answer = lookRes.Text }), false, null);
            }

            case "lookup_term":
            {
                var term = args.TryGetProperty("term", out var t) ? t.GetString() ?? "" : "";
                var route = ResolveRoute(term);
                return (JsonSerializer.Serialize(new { ok = true, term, mappedRoute = route, translated = appText[term] }), false, null);
            }

            case "ask_user":
            {
                var question = args.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
                PendingQuestion = question;
                WaitingForUserAnswer = true;
                _userAnswerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Entries.Add(new AgentChatEntry("question", question));
                NotifyChanged();

                using var reg = ct.Register(() => _userAnswerTcs.TrySetCanceled(ct));
                var answer = await _userAnswerTcs.Task;
                return (JsonSerializer.Serialize(new { ok = true, answer }), false, null);
            }

            case "finish":
            {
                var summary = args.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                return (JsonSerializer.Serialize(new { ok = true }), true, summary);
            }

            default:
                return (JsonSerializer.Serialize(new { ok = false, error = $"Unknown tool '{name}'." }), false, null);
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

    private (string Text, string Risk) LookupActionFromSnapshot(string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(_latestSnapshotJson);
            if (doc.RootElement.TryGetProperty("actions", out var actionsEl))
            {
                foreach (var a in actionsEl.EnumerateArray())
                {
                    if (a.TryGetProperty("id", out var idEl) && idEl.GetString() == id)
                    {
                        var txt = a.TryGetProperty("text", out var t) ? t.GetString() ?? id : id;
                        var risk = a.TryGetProperty("risk", out var r) ? r.GetString() ?? "save" : "save";
                        return (txt, risk);
                    }
                }
            }
        }
        catch { }
        return (id, "save");
    }

    private static bool IsInDialog(string snapshotJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            return doc.RootElement.TryGetProperty("inDialog", out var el) && el.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private static (bool InDialog, bool HasErrors) CheckDialogAndErrors(string snapshotJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            var inDialog = doc.RootElement.TryGetProperty("inDialog", out var d) && d.ValueKind == JsonValueKind.True;
            var hasErrors = doc.RootElement.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0;
            return (inDialog, hasErrors);
        }
        catch { return (false, false); }
    }

    private string AppendLedger(string summary)
    {
        if (_ledger.Count == 0) return summary;
        return $"{summary}\n\n---\n**Actions executed:** {string.Join(" → ", _ledger)}";
    }

    private static string? ResolveRoute(string input)
    {
        var key = input.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        if (key.StartsWith('/')) return key;
        if (PageRoutes.TryGetValue(key, out var exact)) return exact;

        foreach (var pair in PageRoutes)
        {
            if (pair.Key.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                key.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }
        return null;
    }

    private async Task<string> CaptureSnapshotAsync()
    {
        return await jsRuntime.InvokeAsync<string>("crmAgentDriver.captureSnapshot");
    }

    private async Task<AgentStepResponseDto?> PostStepAsync(List<string> messages, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/agent/step")
        {
            Content = JsonContent.Create(new { messages })
        };
        await AttachBearerIfPresentAsync(req);
        using var res = await httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<AgentStepResponseDto>(cancellationToken: ct);
    }

    private async Task<AgentLookResponseDto?> PostLookAsync(string text, string question, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/agent/look")
        {
            Content = JsonContent.Create(new { text, question })
        };
        await AttachBearerIfPresentAsync(req);
        using var res = await httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<AgentLookResponseDto>(cancellationToken: ct);
    }

    private async Task AttachBearerIfPresentAsync(HttpRequestMessage req)
    {
        if (authenticationStateProvider is IdentityAuthenticationStateProvider idProvider)
        {
            var token = await idProvider.GetBearerTokenAsync();
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
    }

    private void AccumulateUsage(AgentUsageDto? usage)
    {
        if (usage == null) return;
        TotalCalls += usage.Calls;
        TotalInputTokens += usage.InputTokens;
        TotalOutputTokens += usage.OutputTokens;
        if (usage.Cost.HasValue) TotalCost += usage.Cost.Value;
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
            catch { }
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
                    var name = tc.GetProperty("function").GetProperty("name").GetString();
                    var args = tc.GetProperty("function").GetProperty("arguments").GetString();
                    Entries.Add(new AgentChatEntry("tool", $"[server] {name}({args})"));
                }
            }
        }
        catch { }
    }

    private void NotifyChanged() => OnStateChanged?.Invoke();

    public enum PermissionDecision
    {
        AllowOnce,
        AllowAlways,
        Deny
    }

    public sealed record AgentChatEntry(string Kind, string Text);
    public sealed record AgentChatSummaryDto(string ChatKey, string Title, DateTime UpdatedDate);
    private sealed record AgentChatListDto(List<AgentChatSummaryDto>? Chats);
    private sealed record AgentChatDetailDto(string? ChatKey, string? Title, string? HistoryJson, string? LogJson);

    private sealed class AgentStatusDto
    {
        public bool UseAgent { get; set; }
    }

    private sealed class AgentStepResponseDto
    {
        public string? Assistant { get; set; }
        public List<string>? Prior { get; set; }
        public List<string>? ServerToolResults { get; set; }
        public AgentUsageDto? Usage { get; set; }
        public string? Error { get; set; }
    }

    private sealed class AgentLookResponseDto
    {
        public string? Text { get; set; }
        public AgentUsageDto? Usage { get; set; }
        public string? Error { get; set; }
    }

    private sealed class AgentUsageDto
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int Calls { get; set; }
        public decimal? Cost { get; set; }
    }
}
