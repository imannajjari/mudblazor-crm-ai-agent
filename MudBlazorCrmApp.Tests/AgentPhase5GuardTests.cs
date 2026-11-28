using System.Text.Json;
using System.Text.Json.Nodes;
using MudBlazorCrmApp.Shared.Blazor.Services.Agent;
using Xunit;

namespace MudBlazorCrmApp.Tests;

public class AgentPhase5GuardTests
{
    [Fact]
    public void RepeatTracker_BlocksFourthExactRepeatOnlyWhenScreenUnchanged()
    {
        var tracker = new AgentTaskRepeatTracker();
        for (var i = 0; i < AgentTaskRepeatTracker.MaxSameCall; i++)
            tracker.Record("click_action", """{"id":"btn_next"}""", "fp_a");

        Assert.NotNull(tracker.PreBlockReason("click_action", """{"id":"btn_next"}"""));

        tracker.NoteFingerprint("fp_b");
        Assert.Null(tracker.PreBlockReason("click_action", """{"id":"btn_next"}"""));
    }

    [Fact]
    public void RepeatTracker_BlocksSameCallAfterAnywhereCapDespiteFingerprintChanges()
    {
        var tracker = new AgentTaskRepeatTracker();
        for (var i = 0; i < AgentTaskRepeatTracker.MaxSameCallAnywhere; i++)
            tracker.Record("click_action", """{"id":"btn_page"}""", "fp_" + i);

        Assert.NotNull(tracker.PreBlockReason("click_action", """{"id":"btn_page"}"""));
        Assert.Contains(MaxAnywhereText, tracker.PreBlockReason("click_action", """{"id":"btn_page"}""")!);
    }

    private const string MaxAnywhereText = "15";

    [Fact]
    public void RepeatTracker_AppliesSelectTreeNodeToolCapAndLeavesOtherToolsUncapped()
    {
        var tracker = new AgentTaskRepeatTracker();
        for (var i = 0; i < AgentTaskRepeatTracker.MaxSelectTreeNodeCalls; i++)
            tracker.Record("select_tree_node", $$"""{"label":"node{{i}}"}""", "fp_" + i);
        Assert.NotNull(tracker.PreBlockReason("select_tree_node", """{"label":"node99"}"""));

        for (var i = 0; i < AgentTaskRepeatTracker.MaxSelectTreeNodeCalls + 5; i++)
            tracker.Record("click_action", $$"""{"id":"a{{i}}"}""", "fp_" + i);
        Assert.Null(tracker.PreBlockReason("click_action", """{"id":"zz"}"""));
    }

    [Fact]
    public void RepeatTracker_ExtractFingerprint_ReadsRootAndNestedScopesAndSurvivesJunk()
    {
        Assert.Equal("fp1", AgentTaskRepeatTracker.ExtractFingerprint("""{"ok":true,"fingerprint":"fp1"}"""));
        Assert.Equal("fp2", AgentTaskRepeatTracker.ExtractFingerprint("""{"result":{"ok":true,"fingerprint":"fp2"},"snapshot":{"a":1}}"""));
        Assert.Equal("fp3", AgentTaskRepeatTracker.ExtractFingerprint("""{"snapshot":{"fingerprint":"fp3"}}"""));
        Assert.Null(AgentTaskRepeatTracker.ExtractFingerprint("not json"));
        Assert.Null(AgentTaskRepeatTracker.ExtractFingerprint(null));
        Assert.Null(AgentTaskRepeatTracker.ExtractFingerprint("""{"ok":true}"""));
    }

    [Fact]
    public void RepeatTracker_CanonicalizesJsonArgsSoReformattingCannotEvadeCaps()
    {
        var tracker = new AgentTaskRepeatTracker();
        tracker.Record("click_action", """{"id":"x"}""", "fp_a");
        tracker.Record("click_action", """{  "id" : "x" }""", "fp_a");
        tracker.Record("click_action", """{"id":"x"}""", "fp_a");

        Assert.NotNull(tracker.PreBlockReason("click_action", """{"id":  "x"}"""));

        tracker.Record("set_field", """{"value":"ب","id":"f"}""", "fp_z");
        tracker.Record("set_field", """{"id":"f","value":"ب"}""", "fp_z");
        tracker.Record("set_field", """{"id":"f","value":"ب"}""", "fp_z");
        Assert.NotNull(tracker.PreBlockReason("set_field", """{"value":"ب","id":"f"}"""));
    }

    [Fact]
    public void SafetyState_UserQuoteEvidence_MatchesGoalAndAnswersVerbatimOnly()
    {
        var safety = new AgentSafetyState();
        safety.SetUserEvidence("ثبت مشتری با مقدار  TBD  انجام بده");
        safety.AppendUserEvidence("مقدار جایگزین: 42");

        Assert.True(safety.IsUserQuote("TBD"));
        Assert.True(safety.IsUserQuote("مقدار جایگزین: 42"));
        Assert.False(safety.IsUserQuote("user"));
        Assert.False(safety.IsUserQuote("Globex"));
        Assert.False(safety.IsUserQuote("ab"));
        Assert.False(safety.IsUserQuote(null));
        Assert.False(safety.IsUserQuote("TBDX"));
        safety.SetUserEvidence("TBDX چیزی نیست");
        Assert.False(safety.IsUserQuote("TBD"));
        Assert.True(safety.IsUserQuote("TBDX"));

        safety.Reset();
        Assert.False(safety.IsUserQuote("TBD"));
    }

    [Fact]
    public async Task SetFieldHandler_PlaceholderNeedsRealUserQuoteAsDelegatedBy()
    {
        var host = new StubUiHost();
        var safety = new AgentSafetyState();
        safety.SetUserEvidence("ثبت مشتری موقت با مقدار TBD");
        var snapshot = """{"url":"/Customer","inDialog":false,"fields":[{"id":"f1","label":"Name","kind":"text"}]}""";
        var ctx = new UiToolContext(snapshot, AgentMode.Auto, [], safety,
            (_, _, _) => Task.FromResult(PermissionDecision.AllowOnce),
            (_, _, _) => Task.FromResult("ok"),
            (_, _, _, _, _) => Task.CompletedTask, host);

        var bareUser = await new SetFieldHandler().ExecuteAsync(
            JsonDocument.Parse("""{"id":"f1","value":"TBD","delegatedBy":"user"}""").RootElement, ctx, CancellationToken.None);
        Assert.Contains("\"blocked\":true", bareUser.ResultJson);

        var invented = await new SetFieldHandler().ExecuteAsync(
            JsonDocument.Parse("""{"id":"f1","value":"TBD","delegatedBy":"قبلاً گفتیم TBD باشد"}""").RootElement, ctx, CancellationToken.None);
        Assert.Contains("\"blocked\":true", invented.ResultJson);

        var quoted = await new SetFieldHandler().ExecuteAsync(
            JsonDocument.Parse("""{"id":"f1","value":"TBD","delegatedBy":"مقدار TBD"}""").RootElement, ctx, CancellationToken.None);
        Assert.DoesNotContain("\"blocked\":true", quoted.ResultJson);
    }

    [Fact]
    public void LoopGuards_ModeAdvice_DetectsAndStripsButIgnoresAutocomplete()
    {
        Assert.True(AgentLoopGuards.AdvisesModeSwitch("لطفاً حالت Auto را انتخاب کنید تا سریع‌تر شود."));
        Assert.True(AgentLoopGuards.AdvisesModeSwitch("You should switch to Auto mode to let me finish."));
        Assert.False(AgentLoopGuards.AdvisesModeSwitch("I used the autocomplete field to pick the city."));
        Assert.False(AgentLoopGuards.AdvisesModeSwitch("مشتری آریا ثبت شد."));
        Assert.False(AgentLoopGuards.AdvisesModeSwitch("حالت خودکار پیامک فعال شد و ذخیره گردید."));

        var stripped = AgentLoopGuards.StripModeAdvice("مشتری آریا ثبت شد. لطفاً حالت Auto را انتخاب کنید. فیلدها پر شدند.");
        Assert.DoesNotContain("حالت Auto", stripped, StringComparison.Ordinal);
        Assert.Contains("مشتری آریا ثبت شد", stripped, StringComparison.Ordinal);
        Assert.Contains("فیلدها پر شدند", stripped, StringComparison.Ordinal);

        var oneClause = AgentLoopGuards.StripModeAdvice("کار انجام شد، لطفاً حالت Auto را انتخاب کنید.");
        Assert.Equal("کار انجام شد", oneClause);
    }

    [Fact]
    public async Task FinishHandler_NudgesOnceThenStripsModeAdviceFromSummary()
    {
        var host = new StubUiHost();
        var safety = new AgentSafetyState();
        var ctx = new UiToolContext("""{"url":"/Customer","inDialog":false,"fields":[]}""", AgentMode.SemiAuto, [], safety,
            (_, _, _) => Task.FromResult(PermissionDecision.AllowOnce),
            (_, _, _) => Task.FromResult("ok"),
            (_, _, _, _, _) => Task.CompletedTask, host);

        var args = JsonDocument.Parse("""{"summary":"کار انجام شد. لطفاً حالت Auto را انتخاب کنید."}""").RootElement;
        var first = await new FinishHandler().ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.False(first.IsFinish);
        Assert.Contains("\"blocked\":true", first.ResultJson);

        var second = await new FinishHandler().ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.True(second.IsFinish);
        Assert.DoesNotContain("حالت Auto", second.Summary, StringComparison.Ordinal);
        Assert.Contains("کار انجام شد", second.Summary, StringComparison.Ordinal);
    }

    private sealed class StubUiHost : IAgentUiHost
    {
        public Task<string> CaptureSnapshotAsync(CancellationToken ct = default) => Task.FromResult("{}");
        public Task<(string? Route, NavRouteDto[] Available)> ResolveRouteAsync(string input, CancellationToken ct) => Task.FromResult<(string?, NavRouteDto[])>((null, []));
        public Task<string> InvokeDriverStringAsync(string fn, CancellationToken ct, params object?[] args) => Task.FromResult("""{"ok":true,"fieldValue":"TBD"}""");
        public Task InvokeDriverVoidAsync(string fn, CancellationToken ct, params object?[] args) => Task.CompletedTask;
        public void NavigateTo(string route) { }
        public Task<LookResponseDto?> PostLookAsync(string text, string question, CancellationToken ct) => Task.FromResult<LookResponseDto?>(null);
        public Task<AttachmentDownloadDto?> GetAttachmentAsync(string id, CancellationToken ct) => Task.FromResult<AttachmentDownloadDto?>(null);
        public (string Url, bool InDialog) ReadUrlAndDialog(string snapshotJson) => ("", false);
        public (string Text, string Risk) LookupAction(string snapshotJson, string id) => (id, "save");
        public string LookupFieldLabel(string snapshotJson, string id) => id;
        public bool EvaluateSaveIncomplete(string clickResultJson, string snapshotJson, bool wasInDialog, string risk) => false;
        public string Translate(string key) => key;
        public string LookupTerm(string term) => term;
        public JsonNode? TryParseNode(string json) { try { return JsonNode.Parse(json); } catch (JsonException) { return null; } }
        public Task<(bool Allowed, string? BlockReason)> AuthorizeAsync(AgentMode mode, string route, bool inDialog, string actionText, string risk, string targetName, string snapshotJson, Func<string, string, CancellationToken, Task<PermissionDecision>> askPermissionAsync, CancellationToken ct) => Task.FromResult<(bool, string?)>((true, null));
    }
}
