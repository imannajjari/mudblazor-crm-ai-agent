using System.Text.Json;
using System.Text.Json.Nodes;

namespace MudBlazorCrmApp.Shared.Blazor.Services.Agent;

public sealed record UiToolResult(string ResultJson, bool IsFinish, string? Summary, string UpdatedSnapshot);

public sealed record NavRouteDto(string Href, string Text);

public sealed record LookResponseDto(string? Text, LookUsageDto? Usage, string? Error);

public sealed record LookUsageDto(int InputTokens, int OutputTokens, int CachedInputTokens, int Calls, decimal? Cost);

public interface IAgentUiHost
{
    Task<string> CaptureSnapshotAsync(CancellationToken ct = default);
    Task<(string? Route, NavRouteDto[] Available)> ResolveRouteAsync(string input, CancellationToken ct);
    Task<string> InvokeDriverStringAsync(string fn, CancellationToken ct, params object?[] args);
    Task InvokeDriverVoidAsync(string fn, CancellationToken ct, params object?[] args);
    void NavigateTo(string route);
    Task<LookResponseDto?> PostLookAsync(string text, string question, CancellationToken ct);
    (string Url, bool InDialog) ReadUrlAndDialog(string snapshotJson);
    (string Text, string Risk) LookupAction(string snapshotJson, string id);
    bool EvaluateSaveIncomplete(string clickResultJson, string snapshotJson, bool wasInDialog, string risk);
    string Translate(string key);
    JsonNode? TryParseNode(string json);
    Task<(bool Allowed, string? BlockReason)> AuthorizeAsync(
        AgentMode mode, string route, bool inDialog, string actionText, string risk, string targetName,
        string snapshotJson, Func<string, string, CancellationToken, Task<PermissionDecision>> askPermissionAsync, CancellationToken ct);
}

public sealed record UiToolContext(
    string CurrentSnapshotJson,
    AgentMode Mode,
    List<string> Ledger,
    AgentSafetyState Safety,
    Func<string, string, CancellationToken, Task<PermissionDecision>> AskPermissionAsync,
    Func<string, CancellationToken, Task<string>> AskUserAsync,
    Func<int, int, int, int, decimal?, Task> OnLookUsageAsync,
    IAgentUiHost Host);

public interface IUiToolHandler
{
    string ToolName { get; }
    Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct);
}

internal static class UiToolArgs
{
    public static string GetString(JsonElement args, string prop)
    {
        if (!args.TryGetProperty(prop, out var el)) return string.Empty;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.ToString(),
            _ => string.Empty
        };
    }
}

public sealed class ScanScreenHandler : IUiToolHandler
{
    public string ToolName => "scan_screen";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        await ctx.Host.InvokeDriverVoidAsync("waitForSettle", ct, 800);
        var snap = await ctx.Host.CaptureSnapshotAsync(ct);
        return new(JsonSerializer.Serialize(new { ok = true, snapshot = ctx.Host.TryParseNode(snap) }), false, null, snap);
    }
}

public sealed class GoToPageHandler : IUiToolHandler
{
    public string ToolName => "go_to_page";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var unsavedErr = ctx.Safety.CheckFinishOrNavAllowed();
        if (unsavedErr != null)
            return new(JsonSerializer.Serialize(new { ok = false, blocked = true, reason = unsavedErr }), false, null, ctx.CurrentSnapshotJson);

        var target = UiToolArgs.GetString(args, "name");
        var (route, available) = await ctx.Host.ResolveRouteAsync(target, ct);
        if (route == null)
            return new(JsonSerializer.Serialize(new { ok = false, error = $"Unknown or blocked page '{target}'.", availableRoutes = available }), false, null, ctx.CurrentSnapshotJson);

        ctx.Host.NavigateTo(route);
        await ctx.Host.InvokeDriverVoidAsync("waitForSettle", ct, 1500);
        var snap = await ctx.Host.CaptureSnapshotAsync(ct);
        return new(JsonSerializer.Serialize(new { ok = true, navigatedTo = route, snapshot = ctx.Host.TryParseNode(snap) }), false, null, snap);
    }
}

public sealed class SelectTabHandler : IUiToolHandler
{
    public string ToolName => "select_tab";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var res = await ctx.Host.InvokeDriverStringAsync("selectTab", ct, UiToolArgs.GetString(args, "id"));
        var snap = await ctx.Host.CaptureSnapshotAsync(ct);
        return new(JsonSerializer.Serialize(new { result = ctx.Host.TryParseNode(res), snapshot = ctx.Host.TryParseNode(snap) }), false, null, snap);
    }
}

public sealed class SetFieldHandler : IUiToolHandler
{
    public string ToolName => "set_field";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var id = UiToolArgs.GetString(args, "id");
        var val = UiToolArgs.GetString(args, "value");
        if (!AgentSafetyState.IsProvidedValue(val))
            return new(JsonSerializer.Serialize(new { ok = false, blocked = true, reason = $"Placeholder or empty value '{val}' is not allowed. Ask the user if missing." }), false, null, ctx.CurrentSnapshotJson);

        var res = await ctx.Host.InvokeDriverStringAsync("setField", ct, id, val);
        ctx.Safety.MarkDirty();
        ctx.Ledger.Add($"set_field({id} = \"{val}\")");
        var snap = await ctx.Host.CaptureSnapshotAsync(ct);
        return new(JsonSerializer.Serialize(new { result = ctx.Host.TryParseNode(res), snapshot = ctx.Host.TryParseNode(snap) }), false, null, snap);
    }
}

public sealed class OpenComboHandler : IUiToolHandler
{
    public string ToolName => "open_combo";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var res = await ctx.Host.InvokeDriverStringAsync("openCombo", ct, UiToolArgs.GetString(args, "id"));
        return new(res, false, null, ctx.CurrentSnapshotJson);
    }
}

public sealed class PickOptionHandler : IUiToolHandler
{
    public string ToolName => "pick_option";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var opt = UiToolArgs.GetString(args, "option");
        if (!AgentSafetyState.IsProvidedValue(opt))
            return new(JsonSerializer.Serialize(new { ok = false, blocked = true, reason = $"Placeholder option '{opt}' is not allowed." }), false, null, ctx.CurrentSnapshotJson);

        var res = await ctx.Host.InvokeDriverStringAsync("pickOption", ct, opt);
        ctx.Safety.MarkDirty();
        ctx.Ledger.Add($"pick_option(\"{opt}\")");
        var snap = await ctx.Host.CaptureSnapshotAsync(ct);
        return new(JsonSerializer.Serialize(new { result = ctx.Host.TryParseNode(res), snapshot = ctx.Host.TryParseNode(snap) }), false, null, snap);
    }
}

public sealed class CloseComboHandler : IUiToolHandler
{
    public string ToolName => "close_combo";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var res = await ctx.Host.InvokeDriverStringAsync("closeCombo", ct);
        return new(res, false, null, ctx.CurrentSnapshotJson);
    }
}

public sealed class ClickActionHandler : IUiToolHandler
{
    public string ToolName => "click_action";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var id = UiToolArgs.GetString(args, "id");
        var targetName = UiToolArgs.GetString(args, "targetName");
        var (route, wasInDialog) = ctx.Host.ReadUrlAndDialog(ctx.CurrentSnapshotJson);
        var (actionText, risk) = ctx.Host.LookupAction(ctx.CurrentSnapshotJson, id);

        if (risk == "save")
        {
            var missingReq = AgentSafetyState.FindEmptyRequiredField(ctx.CurrentSnapshotJson);
            if (missingReq != null)
                return new(JsonSerializer.Serialize(new { ok = false, blocked = true, reason = $"Required field '{missingReq}' is empty. Fill it or ask the user before saving." }), false, null, ctx.CurrentSnapshotJson);
        }

        if (risk == "destructive")
        {
            var deleteBlocker = AgentSafetyState.CheckDeleteBlockers(ctx.CurrentSnapshotJson, targetName);
            if (deleteBlocker != null)
                return new(JsonSerializer.Serialize(new { ok = false, blocked = true, deleteRefused = true, reason = deleteBlocker }), false, null, ctx.CurrentSnapshotJson);
        }

        var (allowed, blockReason) = await ctx.Host.AuthorizeAsync(
            ctx.Mode, route, wasInDialog, actionText, risk, targetName, ctx.CurrentSnapshotJson, ctx.AskPermissionAsync, ct);
        if (!allowed)
            return new(JsonSerializer.Serialize(new { ok = false, blocked = true, deleteRefused = risk == "destructive", reason = blockReason }), false, null, ctx.CurrentSnapshotJson);

        var res = await ctx.Host.InvokeDriverStringAsync("clickAction", ct, id);
        ctx.Ledger.Add($"click_action({actionText} [{risk}])");
        var snap = await ctx.Host.CaptureSnapshotAsync(ct);
        var saveDidNotComplete = ctx.Host.EvaluateSaveIncomplete(res, snap, wasInDialog, risk);
        var (_, nowInDialog) = ctx.Host.ReadUrlAndDialog(snap);
        if ((risk is "save" or "destructive" && !saveDidNotComplete) || (wasInDialog && !nowInDialog))
            ctx.Safety.MarkSaved();
        return new(JsonSerializer.Serialize(new { result = ctx.Host.TryParseNode(res), saveDidNotComplete, snapshot = ctx.Host.TryParseNode(snap) }), false, null, snap);
    }
}

public sealed class ReadGridHeadersHandler : IUiToolHandler
{
    public string ToolName => "read_grid_headers";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var find = args.TryGetProperty("find", out _) ? UiToolArgs.GetString(args, "find") : null;
        var res = await ctx.Host.InvokeDriverStringAsync("readGridHeaders", ct, find);
        return new(res, false, null, ctx.CurrentSnapshotJson);
    }
}

public sealed class SelectGridRowHandler : IUiToolHandler
{
    public string ToolName => "select_grid_row";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var res = await ctx.Host.InvokeDriverStringAsync("selectGridRow", ct, UiToolArgs.GetString(args, "rowId"));
        var snap = await ctx.Host.CaptureSnapshotAsync(ct);
        return new(JsonSerializer.Serialize(new { result = ctx.Host.TryParseNode(res), snapshot = ctx.Host.TryParseNode(snap) }), false, null, snap);
    }
}

public sealed class LookAtScreenHandler : IUiToolHandler
{
    public string ToolName => "look_at_screen";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var question = UiToolArgs.GetString(args, "question");
        var visibleText = await ctx.Host.InvokeDriverStringAsync("captureVisibleText", ct);
        var lookRes = await ctx.Host.PostLookAsync(visibleText, question, ct);
        if (lookRes?.Usage != null)
            await ctx.OnLookUsageAsync(lookRes.Usage.Calls, lookRes.Usage.InputTokens, lookRes.Usage.OutputTokens, lookRes.Usage.CachedInputTokens, lookRes.Usage.Cost);
        if (lookRes == null || !string.IsNullOrWhiteSpace(lookRes.Error))
            return new(JsonSerializer.Serialize(new { ok = false, error = lookRes?.Error ?? "Look failed." }), false, null, ctx.CurrentSnapshotJson);
        return new(JsonSerializer.Serialize(new { ok = true, answer = lookRes.Text }), false, null, ctx.CurrentSnapshotJson);
    }
}

public sealed class LookupTermHandler : IUiToolHandler
{
    public string ToolName => "lookup_term";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var term = UiToolArgs.GetString(args, "term");
        var (route, _) = await ctx.Host.ResolveRouteAsync(term, ct);
        return new(JsonSerializer.Serialize(new { ok = true, term, mappedRoute = route, translated = ctx.Host.Translate(term) }), false, null, ctx.CurrentSnapshotJson);
    }
}

public sealed class AskUserHandler : IUiToolHandler
{
    public string ToolName => "ask_user";
    public async Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var answer = await ctx.AskUserAsync(UiToolArgs.GetString(args, "question"), ct);
        return new(JsonSerializer.Serialize(new { ok = true, answer }), false, null, ctx.CurrentSnapshotJson);
    }
}

public sealed class FinishHandler : IUiToolHandler
{
    public string ToolName => "finish";
    public Task<UiToolResult> ExecuteAsync(JsonElement args, UiToolContext ctx, CancellationToken ct)
    {
        var unsavedErr = ctx.Safety.CheckFinishOrNavAllowed();
        if (unsavedErr != null)
            return Task.FromResult(new UiToolResult(JsonSerializer.Serialize(new { ok = false, blocked = true, reason = unsavedErr }), false, null, ctx.CurrentSnapshotJson));

        return Task.FromResult(new UiToolResult(JsonSerializer.Serialize(new { ok = true }), true, UiToolArgs.GetString(args, "summary"), ctx.CurrentSnapshotJson));
    }
}
