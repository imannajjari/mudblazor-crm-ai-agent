using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazorCrmApp.Data;
using MudBlazorCrmApp.Models;
using MudBlazorCrmApp.Services.Agent;
using MudBlazorCrmApp.Shared.Blazor.Services.Agent;
using Xunit;

namespace MudBlazorCrmApp.Tests;

public class AgentServiceTests
{
    [Fact]
    public void TryParseMessages_RejectsSystemRoleAndInvalidJson()
    {
        Assert.False(AgentService.TryParseMessages(null, out _));
        Assert.False(AgentService.TryParseMessages([], out _));
        Assert.False(AgentService.TryParseMessages(["not-json"], out _));
        Assert.False(AgentService.TryParseMessages(["""{"role":"system","content":"hi"}"""], out _));
        Assert.False(AgentService.TryParseMessages(["""{"role":"tool","content":"missing id"}"""], out _));
    }

    [Fact]
    public void TryParseMessages_AcceptsValidUserAssistantToolTranscript()
    {
        var raw = new List<string>
        {
            """{"role":"user","content":"سلام"}""",
            """{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"scan_screen","arguments":"{}"}}]}""",
            """{"role":"tool","tool_call_id":"c1","content":"{\"ok\":true}"}"""
        };

        var ok = AgentService.TryParseMessages(raw, out var parsed);
        Assert.True(ok);
        Assert.Equal(3, parsed.Count);
        Assert.Equal("user", parsed[0]["role"]?.GetValue<string>());
    }

    [Fact]
    public void AgentOptions_Normalize_FixesInvalidBounds()
    {
        var opts = new AgentOptions
        {
            BaseUrl = "https://example.com/v1",
            Model = "",
            MaxCallsPerUserPerMinute = -5,
            MaxTaskModelCalls = 999,
            MaxTaskCostUsd = -1m,
            TimeoutSeconds = 0
        };

        opts.Normalize();

        Assert.Equal("https://example.com/v1/", opts.BaseUrl);
        Assert.Equal("gemini-3.5-flash-lite", opts.Model);
        Assert.Equal(60, opts.MaxCallsPerUserPerMinute);
        Assert.Equal(40, opts.MaxTaskModelCalls);
        Assert.Equal(0.80m, opts.MaxTaskCostUsd);
        Assert.Equal(90, opts.TimeoutSeconds);
    }

    [Fact]
    public void Sqlite_AgentChat_RoundTripsUnicodePersianText()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        using var db = new ApplicationDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();

        var persianTitle = "گزارش مشتریان و سرنخ‌های فروش ۱۴۰۵";
        db.AgentChat.Add(new AgentChat
        {
            UserId = "u1",
            ChatKey = "c_test1",
            Title = persianTitle,
            HistoryJson = "[{\"Kind\":\"user\",\"Text\":\"سلام\"}]",
            LogJson = "[]"
        });
        db.SaveChanges();

        var loaded = db.AgentChat.Single(c => c.ChatKey == "c_test1");
        Assert.Equal(persianTitle, loaded.Title);
        Assert.Contains("سلام", loaded.HistoryJson);
    }

    [Fact]
    public void AgentCostCalculator_ClampsCachedTokensAndRespectsShowCost()
    {
        var opts = new AgentOptions { ShowCost = true, InputPricePerMillionTokensUsd = 0.10m, OutputPricePerMillionTokensUsd = 0.40m };
        var usage = new AgentUsageModel { Calls = 1, InputTokens = 1000, CachedInputTokens = 2000, OutputTokens = 500 };

        AgentCostCalculator.Compute(usage, opts);
        Assert.NotNull(usage.Cost);
        Assert.True(usage.Cost > 0m);

        opts.ShowCost = false;
        AgentCostCalculator.Compute(usage, opts);
        Assert.Null(usage.Cost);
    }

    [Fact]
    public void MemoryCacheRateLimiter_EnforcesPerMinuteLimit()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new MemoryCacheRateLimiter(cache);

        Assert.True(limiter.TryAcquire("u1", 2));
        Assert.True(limiter.TryAcquire("u1", 2));
        Assert.False(limiter.TryAcquire("u1", 2));
        Assert.True(limiter.TryAcquire("u2", 2));
    }

    [Fact]
    public void TryParseMessages_RejectsUnmatchedToolCallId()
    {
        var raw = new List<string>
        {
            """{"role":"user","content":"test"}""",
            """{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"scan_screen","arguments":"{}"}}]}""",
            """{"role":"tool","tool_call_id":"wrong_id","content":"{\"ok\":true}"}"""
        };

        Assert.False(AgentService.TryParseMessages(raw, out _));
    }

    [Fact]
    public void AgentServerGuard_EnforcesPagingAskHelpAndDeleteRefusal()
    {
        var guard = new AgentServerGuard();
        using var page1 = JsonDocument.Parse("""{"source":"customers","skip":10}""");
        using var page2 = JsonDocument.Parse("""{"source":"customers","skip":20}""");
        using var page3 = JsonDocument.Parse("""{"source":"customers","skip":30}""");

        Assert.Null(guard.CheckBeforeExecute("read_data", page1.RootElement.GetRawText(), page1.RootElement));
        Assert.Null(guard.CheckBeforeExecute("read_data", page2.RootElement.GetRawText(), page2.RootElement));
        var blockedPage = guard.CheckBeforeExecute("read_data", page3.RootElement.GetRawText(), page3.RootElement);
        Assert.NotNull(blockedPage);
        Assert.Contains("Paging walk limit", blockedPage);

        using var emptyArgs = JsonDocument.Parse("{}");
        for (var i = 1; i <= 3; i++)
            Assert.Null(guard.CheckBeforeExecute("ask_help", $"{{\"question\":\"q{i}\"}}", emptyArgs.RootElement));
        var blockedHelp = guard.CheckBeforeExecute("ask_help", """{"question":"q4"}""", emptyArgs.RootElement);
        Assert.NotNull(blockedHelp);
        Assert.Contains("ask_help limit", blockedHelp);

        var prior = new List<JsonObject>
        {
            JsonNode.Parse("""{"role":"tool","tool_call_id":"c1","content":"{\"ok\":false,\"deleteRefused\":true}"}""")!.AsObject()
        };
        var refusedGuard = AgentServerGuard.FromTranscript(prior, new HashSet<string> { "read_data" });
        using var freshRead = JsonDocument.Parse("""{"source":"leads"}""");
        var blockedAfterDelete = refusedGuard.CheckBeforeExecute("read_data", freshRead.RootElement.GetRawText(), freshRead.RootElement);
        Assert.NotNull(blockedAfterDelete);
        Assert.Contains("delete refusal", blockedAfterDelete);
    }

    [Fact]
    public void AgentServerGuard_BlocksSameServerErrorTwice()
    {
        var guard = new AgentServerGuard();
        var errJson = """{"ok":false,"error":"Source not found"}""";
        Assert.Equal(errJson, guard.RecordAndCheckError("read_data", errJson));
        var second = guard.RecordAndCheckError("read_data", errJson);
        Assert.Contains("Same server error occurred twice", second);
    }

    [Fact]
    public void SafeMarkdownFormatter_EncodesHtmlAndFormatsMarkdown()
    {
        var html = SafeMarkdownFormatter.ToSafeHtml("تعداد: **۱۴** <script>alert(1)</script> `code`");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<strong>۱۴</strong>", html);
        Assert.Contains("<code ", html);
    }

    [Fact]
    public async Task AgentPermissionGate_EnforcesModesAndDestructiveConfirmation()
    {
        var gate = new AgentPermissionGate();
        var promptCount = 0;
        Task<PermissionDecision> AllowAlways(string _, string __, CancellationToken ___)
        {
            promptCount++;
            return Task.FromResult(PermissionDecision.AllowAlways);
        }

        // Auto mode allows read/write/save without prompting, but still prompts for destructive
        var (okSave, _) = await gate.AuthorizeAsync(AgentMode.Auto, "/Customer", true, "Save", "save", "", "{}", AllowAlways, CancellationToken.None);
        Assert.True(okSave);
        Assert.Equal(0, promptCount);

        // Destructive without matching targetName in snapshot is blocked before prompting
        var (okMissingTarget, reason) = await gate.AuthorizeAsync(AgentMode.Auto, "/Customer", false, "Delete", "destructive", "NonExistent", """{"fields":[],"grids":[]}""", AllowAlways, CancellationToken.None);
        Assert.False(okMissingTarget);
        Assert.Contains("targetName", reason);

        // Destructive with matching targetName in snapshot prompts even in Auto mode
        var (okDelete, _) = await gate.AuthorizeAsync(AgentMode.Auto, "/Customer", false, "Delete", "destructive", "Acme", """{"title":"Acme","fields":[],"grids":[]}""", AllowAlways, CancellationToken.None);
        Assert.True(okDelete);
        Assert.Equal(1, promptCount);
    }

    [Fact]
    public void AgentUiToolDispatcher_MatchRoute_ResolvesSingularPluralAndBlocksAuthRoutes()
    {
        var routes = new[]
        {
            new NavRouteDto("/Customer", "شرکت‌ها / مشتریان"),
            new NavRouteDto("/Lead", "سرنخ‌ها"),
            new NavRouteDto("/logout", "خروج")
        };

        var (custRoute, _) = AgentUiToolDispatcher.MatchRoute("Customers", routes);
        Assert.Equal("/Customer", custRoute);

        var (faRoute, _) = AgentUiToolDispatcher.MatchRoute("مشتریان", routes);
        Assert.Equal("/Customer", faRoute);

        var (blockedRoute, _) = AgentUiToolDispatcher.MatchRoute("/logout", routes);
        Assert.Null(blockedRoute);
    }

    [Fact]
    public void AgentSafetyState_EnforcesUnsavedRequiredValueAndDeleteGuards()
    {
        var safety = new AgentSafetyState();
        Assert.Null(safety.CheckFinishOrNavAllowed());
        safety.MarkDirty();
        Assert.NotNull(safety.CheckFinishOrNavAllowed());
        safety.MarkSaved();
        Assert.Null(safety.CheckFinishOrNavAllowed());

        Assert.False(AgentSafetyState.IsProvidedValue("TBD"));
        Assert.True(AgentSafetyState.IsProvidedValue("شرکت نمونه"));

        var missing = AgentSafetyState.FindEmptyRequiredField("""{"fields":[{"id":"f1","label":"Name","required":true,"value":""}]}""");
        Assert.Equal("Name", missing);

        var blocker = AgentSafetyState.CheckDeleteBlockers("""{"blocksDelete":true,"inUseSummary":"Has 2 invoices"}""", "Acme");
        Assert.Contains("Has 2 invoices", blocker);
    }

    [Fact]
    public async Task AgentService_StepAsync_EnforcesFinalCallAndHowToMode()
    {
        var fakeProvider = new StubLlmProvider();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AgentService(
            fakeProvider,
            new MemoryCacheRateLimiter(cache),
            [],
            Options.Create(new AgentOptions { Enabled = true, MaxTaskModelCalls = 5 }),
            NullLogger<AgentService>.Instance);

        var howToRes = await service.StepAsync(new AgentStepRequestModel
        {
            Messages = ["""{"role":"user","content":"[how-to question] چطور مشتری اضافه کنم؟"}"""]
        }, "u1", CancellationToken.None);

        Assert.Null(howToRes.Error);
        Assert.Equal(3, fakeProvider.LastToolCount);

        var finalRes = await service.StepAsync(new AgentStepRequestModel
        {
            Messages = ["""{"role":"user","content":"[final call] خلاصه بده"}"""]
        }, "u1", CancellationToken.None);

        Assert.Null(finalRes.Error);
        Assert.Null(fakeProvider.LastToolCount);
    }

    private sealed class StubLlmProvider : IAgentLlmProvider
    {
        public int? LastToolCount { get; private set; }

        public Task<LlmCallResult> CompleteAsync(
            IReadOnlyList<JsonObject> messages,
            JsonElement[]? tools,
            CancellationToken cancellationToken)
        {
            LastToolCount = tools?.Length;
            return Task.FromResult(new LlmCallResult(
                """{"role":"assistant","content":"پاسخ تستی"}""",
                new AgentUsageModel { Calls = 1, InputTokens = 10, OutputTokens = 5 },
                null));
        }
    }
}
