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
    ILogger<AgentUiToolDispatcher> logger)
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> BlockedRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/logout", "/account/changepassword", "/login", "/register", "/user"
    };

    public async Task<string> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            return await jsRuntime.InvokeAsync<string>("crmAgentDriver.captureSnapshot", ct);
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
        Func<string, CancellationToken, Task<string>> askUserAsync,
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
        var args = argsDoc.RootElement;

        try
        {
        switch (name)
        {
            case "scan_screen":
            {
                await jsRuntime.InvokeVoidAsync("crmAgentDriver.waitForSettle", ct, 800);
                var snap = await CaptureSnapshotAsync(ct);
                return (JsonSerializer.Serialize(new { ok = true, snapshot = TryParseNode(snap) }), false, null, snap);
            }
            case "go_to_page":
            {
                var target = GetArgString(args, "name");
                var (route, available) = await ResolveRouteAsync(target, ct);
                if (route == null)
                {
                    return (JsonSerializer.Serialize(new { ok = false, error = $"Unknown or blocked page '{target}'.", availableRoutes = available }), false, null, currentSnapshotJson);
                }
                navigationManager.NavigateTo(route);
                await jsRuntime.InvokeVoidAsync("crmAgentDriver.waitForSettle", ct, 1500);
                var snap = await CaptureSnapshotAsync(ct);
                return (JsonSerializer.Serialize(new { ok = true, navigatedTo = route, snapshot = TryParseNode(snap) }), false, null, snap);
            }
            case "select_tab":
            {
                var id = GetArgString(args, "id");
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.selectTab", ct, id);
                var snap = await CaptureSnapshotAsync(ct);
                return (JsonSerializer.Serialize(new { result = TryParseNode(res), snapshot = TryParseNode(snap) }), false, null, snap);
            }
            case "set_field":
            {
                var id = GetArgString(args, "id");
                var val = GetArgString(args, "value");
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.setField", ct, id, val);
                ledger.Add($"set_field({id} = \"{val}\")");
                var snap = await CaptureSnapshotAsync(ct);
                return (JsonSerializer.Serialize(new { result = TryParseNode(res), snapshot = TryParseNode(snap) }), false, null, snap);
            }
            case "open_combo":
            {
                var id = GetArgString(args, "id");
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.openCombo", ct, id);
                return (res, false, null, currentSnapshotJson);
            }
            case "pick_option":
            {
                var opt = GetArgString(args, "option");
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.pickOption", ct, opt);
                ledger.Add($"pick_option(\"{opt}\")");
                var snap = await CaptureSnapshotAsync(ct);
                return (JsonSerializer.Serialize(new { result = TryParseNode(res), snapshot = TryParseNode(snap) }), false, null, snap);
            }
            case "close_combo":
            {
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.closeCombo", ct);
                return (res, false, null, currentSnapshotJson);
            }
            case "click_action":
            {
                var id = GetArgString(args, "id");
                var targetName = GetArgString(args, "targetName");
                var (route, wasInDialog) = ReadUrlAndDialog(currentSnapshotJson);
                var (actionText, risk) = LookupAction(currentSnapshotJson, id);

                var (allowed, blockReason) = await permissionGate.AuthorizeAsync(
                    mode, route, wasInDialog, actionText, risk, targetName, currentSnapshotJson, askPermissionAsync, ct);
                if (!allowed)
                {
                    return (JsonSerializer.Serialize(new { ok = false, blocked = true, reason = blockReason }), false, null, currentSnapshotJson);
                }

                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.clickAction", ct, id);
                ledger.Add($"click_action({actionText} [{risk}])");
                var snap = await CaptureSnapshotAsync(ct);
                var saveDidNotComplete = EvaluateSaveIncomplete(res, snap, wasInDialog, risk);
                return (JsonSerializer.Serialize(new { result = TryParseNode(res), saveDidNotComplete, snapshot = TryParseNode(snap) }), false, null, snap);
            }
            case "read_grid_headers":
            {
                var find = args.TryGetProperty("find", out _) ? GetArgString(args, "find") : null;
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.readGridHeaders", ct, find);
                return (res, false, null, currentSnapshotJson);
            }
            case "select_grid_row":
            {
                var rowId = GetArgString(args, "rowId");
                var res = await jsRuntime.InvokeAsync<string>("crmAgentDriver.selectGridRow", ct, rowId);
                var snap = await CaptureSnapshotAsync(ct);
                return (JsonSerializer.Serialize(new { result = TryParseNode(res), snapshot = TryParseNode(snap) }), false, null, snap);
            }
            case "look_at_screen":
            {
                var question = GetArgString(args, "question");
                var visibleText = await jsRuntime.InvokeAsync<string>("crmAgentDriver.captureVisibleText", ct);
                var lookRes = await PostLookAsync(visibleText, question, ct);
                if (lookRes?.Usage != null)
                    await onLookUsageAsync(lookRes.Usage.Calls, lookRes.Usage.InputTokens, lookRes.Usage.OutputTokens, lookRes.Usage.CachedInputTokens, lookRes.Usage.Cost);
                if (lookRes == null || !string.IsNullOrWhiteSpace(lookRes.Error))
                    return (JsonSerializer.Serialize(new { ok = false, error = lookRes?.Error ?? "Look failed." }), false, null, currentSnapshotJson);
                return (JsonSerializer.Serialize(new { ok = true, answer = lookRes.Text }), false, null, currentSnapshotJson);
            }
            case "lookup_term":
            {
                var term = GetArgString(args, "term");
                var (route, _) = await ResolveRouteAsync(term, ct);
                return (JsonSerializer.Serialize(new { ok = true, term, mappedRoute = route, translated = appText[term] }), false, null, currentSnapshotJson);
            }
            case "ask_user":
            {
                var question = GetArgString(args, "question");
                var answer = await askUserAsync(question, ct);
                return (JsonSerializer.Serialize(new { ok = true, answer }), false, null, currentSnapshotJson);
            }
            case "finish":
            {
                var summary = GetArgString(args, "summary");
                return (JsonSerializer.Serialize(new { ok = true }), true, summary, currentSnapshotJson);
            }
            default:
                return (JsonSerializer.Serialize(new { ok = false, error = $"Unknown tool '{name}'." }), false, null, currentSnapshotJson);
        }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UI tool '{ToolName}' failed.", name);
            return (JsonSerializer.Serialize(new { ok = false, error = ex.Message }), false, null, currentSnapshotJson);
        }
    }

    private static string GetArgString(JsonElement args, string prop)
    {
        if (!args.TryGetProperty(prop, out var el)) return string.Empty;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.ToString(),
            _ => string.Empty
        };
    }

    private static string NormalizeRoutePath(string href)
    {
        var path = href.Split('?', '#')[0].Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private async Task<(string? Route, NavRouteDto[] Available)> ResolveRouteAsync(string input, CancellationToken ct)
    {
        var key = input.Trim();
        var available = (await GetNavRoutesAsync(ct))
            .Where(r => !BlockedRoutes.Contains(NormalizeRoutePath(r.Href)))
            .ToArray();
        if (string.IsNullOrEmpty(key) || key.StartsWith("//")) return (null, available);

        if (key.StartsWith('/') && BlockedRoutes.Contains(NormalizeRoutePath(key)))
            return (null, available);

        var match = available.FirstOrDefault(r =>
                string.Equals(r.Text, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Href.TrimStart('/'), key.TrimStart('/'), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeRoutePath(r.Href), NormalizeRoutePath(key), StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(r =>
                r.Text.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                key.Contains(r.Text, StringComparison.OrdinalIgnoreCase) ||
                r.Href.TrimStart('/').Contains(key.TrimStart('/'), StringComparison.OrdinalIgnoreCase));

        if (match == null || BlockedRoutes.Contains(NormalizeRoutePath(match.Href)))
            return (null, available);

        return (match.Href, available);
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

    private async Task<LookResponseDto?> PostLookAsync(string text, string question, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/agent/look")
        {
            Content = JsonContent.Create(new { text, question })
        };
        var token = await tokenProvider.GetTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<LookResponseDto>(cancellationToken: ct);
    }

    private static JsonNode? TryParseNode(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static (string Url, bool InDialog) ReadUrlAndDialog(string snapshotJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var inDlg = doc.RootElement.TryGetProperty("inDialog", out var d) && d.ValueKind == JsonValueKind.True;
            return (url, inDlg);
        }
        catch (JsonException) { return ("", false); }
    }

    private static (string Text, string Risk) LookupAction(string snapshotJson, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
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
        catch (JsonException) { }
        return (id, "save");
    }

    private static bool EvaluateSaveIncomplete(string clickResultJson, string snapshotJson, bool wasInDialog, string risk)
    {
        if (risk is not ("save" or "destructive")) return false;
        try
        {
            using var clickDoc = JsonDocument.Parse(clickResultJson);
            if (clickDoc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                return true;

            using var snapDoc = JsonDocument.Parse(snapshotJson);
            var root = snapDoc.RootElement;
            var stillInDialog = root.TryGetProperty("inDialog", out var d) && d.ValueKind == JsonValueKind.True;
            var hasErrors = root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0;
            return (wasInDialog && stillInDialog) || hasErrors;
        }
        catch (JsonException) { return true; }
    }

    private sealed record NavRouteDto(string Href, string Text);
    private sealed record LookResponseDto(string? Text, LookUsageDto? Usage, string? Error);
    private sealed record LookUsageDto(int InputTokens, int OutputTokens, int CachedInputTokens, int Calls, decimal? Cost);
}
