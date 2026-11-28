using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public sealed class AgentUiToolDispatcher(
    HttpClient httpClient,
    IJSRuntime jsRuntime,
    NavigationManager navigationManager,
    IAccessTokenProvider tokenProvider,
    AgentPermissionGate permissionGate,
    AppText appText,
    IEnumerable<IUiToolHandler> handlers,
    ILogger<AgentUiToolDispatcher> logger) : IAgentUiHost
{
    private const string LookEndpoint = "/api/agent/look";
    private const string AttachmentEndpoint = "/api/agent/attachment/";
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] BlockedRoutePrefixes =
    [
        "/logout", "/account", "/login", "/register", "/user", "/auditlog", "/import", "/emailtemplate"
    ];

    public static bool IsBlockedRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return false;
        var norm = NormalizeRoutePath(route);
        return BlockedRoutePrefixes.Any(prefix =>
            string.Equals(norm, prefix, StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    private readonly Dictionary<string, IUiToolHandler> _handlerMap =
        handlers.ToDictionary(h => h.ToolName, StringComparer.Ordinal);
    private readonly AgentSafetyState _safety = new();

    public IReadOnlyCollection<string> HandledToolNames => _handlerMap.Keys;

    public void ResetSafety(bool isDeleteGoal = false, bool hasAttachments = false, bool adviceOnlyGoal = false)
    {
        _safety.Reset();
        _safety.DeleteTaskRequested = isDeleteGoal;
        _safety.HasAttachmentsInTask = hasAttachments;
        _adviceOnlyGoal = adviceOnlyGoal;
    }

    private bool _adviceOnlyGoal;
    public bool IsVisionEnabled { get; set; } = true;
    public void ResetSafety() => ResetSafety(false, false, false);
    public void MarkRecordRead() => _safety.MarkRead();
    public string? CheckFinishAllowed() => _safety.CheckFinishOrNavAllowed();
    public void SetUserEvidence(string? evidence) => _safety.SetUserEvidence(evidence);
    public void AppendUserEvidence(string? text) => _safety.AppendUserEvidence(text);

    public async Task RestoreFiltersAfterTaskAsync(CancellationToken ct = default)
    {
        try { await jsRuntime.InvokeVoidAsync("crmAgentDriver.restoreFilters", ct); }
        catch (Exception ex) { logger.LogDebug(ex, "restoreFilters after task failed."); }
    }

    public Task<string> CaptureSnapshotAsync(CancellationToken ct = default) => CaptureSnapshotAsync(15, ct);

    public async Task<string> CaptureSnapshotAsync(int maxRows, CancellationToken ct = default)
    {
        try
        {
            return await jsRuntime.InvokeAsync<string>("crmAgentDriver.captureSnapshot", ct, maxRows);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "captureSnapshot failed.");
            return "{}";
        }
    }

    public async Task<(string ResultJson, bool IsFinish, string? Summary, string UpdatedSnapshot)> ExecuteAsync(
        string name,
        string argsJson,
        string currentSnapshotJson,
        AgentMode mode,
        List<string> ledger,
        Func<string, string, CancellationToken, Task<PermissionDecision>> askPermissionAsync,
        Func<string, string[]?, CancellationToken, Task<string>> askUserAsync,
        Func<int, int, int, int, decimal?, Task> onLookUsageAsync,
        CancellationToken ct)
    {
        JsonDocument argsDoc;
        try
        {
            argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        }
        catch (JsonException)
        {
            return (JsonSerializer.Serialize(new { ok = false, error = "Invalid JSON tool arguments." }), false, null, currentSnapshotJson);
        }

        using var docScope = argsDoc;
        if (!_handlerMap.TryGetValue(name, out var handler))
            return (JsonSerializer.Serialize(new { ok = false, error = $"Unknown tool '{name}'." }), false, null, currentSnapshotJson);

        try
        {
            var ctx = new UiToolContext(currentSnapshotJson, mode, ledger, _safety, askPermissionAsync, askUserAsync, onLookUsageAsync, this);
            var res = await handler.ExecuteAsync(argsDoc.RootElement, ctx, ct);
            return (res.ResultJson, res.IsFinish, res.Summary, res.UpdatedSnapshot);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UI tool '{ToolName}' failed.", name);
            return (JsonSerializer.Serialize(new { ok = false, error = ex.Message }), false, null, currentSnapshotJson);
        }
    }

    public async Task ClearPanelStateAsync(CancellationToken ct = default)
    {
        try { await jsRuntime.InvokeVoidAsync("crmAgentDriver.clearPanelState", ct, "crm_agent_panel_state"); }
        catch (Exception ex) { logger.LogDebug(ex, "clearPanelState failed."); }
    }

    public Task<string> InvokeDriverStringAsync(string fn, CancellationToken ct, params object?[] args) =>
        jsRuntime.InvokeAsync<string>($"crmAgentDriver.{fn}", ct, args).AsTask();

    public Task InvokeDriverVoidAsync(string fn, CancellationToken ct, params object?[] args) =>
        jsRuntime.InvokeVoidAsync($"crmAgentDriver.{fn}", ct, args).AsTask();

    public void NavigateTo(string route) => navigationManager.NavigateTo(route);
    public string Translate(string key) => appText[key];
    public string LookupTerm(string term) => appText.LookupTerm(term);

    public async Task<AttachmentDownloadDto?> GetAttachmentAsync(string id, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, AttachmentEndpoint + Uri.EscapeDataString(id));
        var token = await tokenProvider.GetTokenAsync();
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await httpClient.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<AttachmentDownloadDto>(CaseInsensitiveJson, ct);
    }

    public Task<(bool Allowed, string? BlockReason)> AuthorizeAsync(
        AgentMode mode, string route, bool inDialog, string actionText, string risk, string targetName,
        string snapshotJson, Func<string, string, CancellationToken, Task<PermissionDecision>> askPermissionAsync, CancellationToken ct) =>
        permissionGate.AuthorizeAsync(mode, route, inDialog, actionText, risk, targetName, snapshotJson, askPermissionAsync, ct, _adviceOnlyGoal);

    public static string NormalizeRoutePath(string href)
    {
        var path = href.Split('?', '#')[0].Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    public static (string? Route, NavRouteDto[] Available) MatchRoute(string input, NavRouteDto[] routes)
    {
        var key = input.Trim();
        var available = routes.Where(r => !IsBlockedRoute(r.Href)).ToArray();
        if (string.IsNullOrEmpty(key) || key.StartsWith("//")) return (null, available);
        if (key.StartsWith('/') && IsBlockedRoute(key)) return (null, available);

        var keyStem = key.TrimStart('/').TrimEnd('s', 'S');
        var match = available.FirstOrDefault(r =>
                string.Equals(r.Text, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Href.TrimStart('/'), key.TrimStart('/'), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Href.TrimStart('/').TrimEnd('s', 'S'), keyStem, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeRoutePath(r.Href), NormalizeRoutePath(key), StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(r =>
                (!string.IsNullOrEmpty(r.Text) && (r.Text.Contains(key, StringComparison.OrdinalIgnoreCase) || key.Contains(r.Text, StringComparison.OrdinalIgnoreCase))) ||
                r.Href.TrimStart('/').Contains(keyStem, StringComparison.OrdinalIgnoreCase) ||
                keyStem.Contains(r.Href.TrimStart('/').TrimEnd('s', 'S'), StringComparison.OrdinalIgnoreCase));

        if (match == null || IsBlockedRoute(match.Href)) return (null, available);
        return (match.Href, available);
    }

    public async Task<(string? Route, NavRouteDto[] Available)> ResolveRouteAsync(string input, CancellationToken ct)
    {
        var routes = await GetNavRoutesAsync(ct);
        return MatchRoute(input, routes);
    }

    private async Task<NavRouteDto[]> GetNavRoutesAsync(CancellationToken ct)
    {
        try
        {
            var json = await jsRuntime.InvokeAsync<string>("crmAgentDriver.getNavRoutes", ct);
            return JsonSerializer.Deserialize<NavRouteDto[]>(json, CaseInsensitiveJson) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read navigation routes from DOM.");
            return [];
        }
    }

    public async Task<LookResponseDto?> PostLookAsync(string text, string question, CancellationToken ct) =>
        await PostLookAsync(text, question, null, null, ct);

    public async Task<LookResponseDto?> PostLookAsync(string text, string question, string? image, string? mediaType, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, LookEndpoint)
        {
            Content = JsonContent.Create(new { text, question, image, mediaType })
        };
        var token = await tokenProvider.GetTokenAsync();
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<LookResponseDto>(cancellationToken: ct);
    }

    public JsonNode? TryParseNode(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException ex) { logger.LogDebug(ex, "Failed to parse JSON node."); return null; }
    }

    public (string Url, bool InDialog) ReadUrlAndDialog(string snapshotJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            var url = doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
            var inDlg = doc.RootElement.TryGetProperty("inDialog", out var d) && d.ValueKind == JsonValueKind.True;
            return (url, inDlg);
        }
        catch (JsonException ex) { logger.LogDebug(ex, "Failed to read URL/dialog from snapshot."); return ("", false); }
    }

    public (string Text, string Risk) LookupAction(string snapshotJson, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            if (doc.RootElement.TryGetProperty("actions", out var actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in actionsEl.EnumerateArray())
                {
                    if (a.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && idEl.GetString() == id)
                    {
                        var txt = a.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? id : id;
                        var risk = a.TryGetProperty("risk", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "save" : "save";
                        return (txt, risk);
                    }
                }
            }
        }
        catch (JsonException ex) { logger.LogDebug(ex, "Failed to look up action {ActionId}.", id); }
        // An id the model never saw in a snapshot is a guess: classify it as the strictest risk so the
        // destructive gate (target must be on screen, delete prerequisites) applies, never as a plain save.
        return (id, "destructive");
    }

    public string LookupFieldLabel(string snapshotJson, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            if (doc.RootElement.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fieldsEl.EnumerateArray())
                {
                    if (f.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && idEl.GetString() == id)
                        return f.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? id : id;
                }
            }
        }
        catch (JsonException ex) { logger.LogDebug(ex, "Failed to look up field {FieldId}.", id); }
        return id;
    }

    public bool EvaluateSaveIncomplete(string clickResultJson, string snapshotJson, bool wasInDialog, string risk)
    {
        if (risk is not ("save" or "destructive")) return false;
        try
        {
            using var clickDoc = JsonDocument.Parse(clickResultJson);
            if (clickDoc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False) return true;
            using var snapDoc = JsonDocument.Parse(snapshotJson);
            var root = snapDoc.RootElement;
            var stillInDialog = root.TryGetProperty("inDialog", out var d) && d.ValueKind == JsonValueKind.True;
            var hasErrors = root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0;
            return (wasInDialog && stillInDialog) || hasErrors;
        }
        catch (JsonException ex) { logger.LogDebug(ex, "Failed to evaluate save completeness."); return true; }
    }
}
