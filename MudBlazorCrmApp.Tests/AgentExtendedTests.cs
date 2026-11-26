using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazorCrmApp.Controllers;
using MudBlazorCrmApp.Services.Agent;
using MudBlazorCrmApp.Shared.Blazor.Services.Agent;
using Xunit;

namespace MudBlazorCrmApp.Tests;

public class AgentExtendedTests
{
    [Fact]
    public void AgentHelpService_SearchesAndRanksSections()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "crm_help_" + Guid.NewGuid().ToString("N"));
        var helpDir = Path.Combine(tempRoot, "HelpDocs");
        Directory.CreateDirectory(helpDir);
        try
        {
            File.WriteAllText(Path.Combine(helpDir, "guide.md"), """
                ## Adding a Customer
                Open Customers page and click Add to create a customer record.

                ## Managing Leads
                Convert qualified leads into opportunities.
                """);

            var svc = new AgentHelpService(new FakeHostEnv(tempRoot));
            var hits = svc.Search("customer add");
            Assert.NotEmpty(hits);
            Assert.Equal("Adding a Customer", hits[0].Title);

            var page = svc.GetPage("Leads");
            Assert.NotNull(page);
            Assert.Contains("Convert qualified leads", page!.Content);

            Assert.Empty(svc.Search("   "));
            Assert.Empty(svc.Search("nonexistent_xyz_token"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AgentHelpLlmAdvisor_ExtractsKeywordsDistillsAndFallsBackOnError()
    {
        var catalog = new StubHelpCatalog([new HelpSearchHit("Customer Guide", "Click Add on /Customer.", 3)]);
        var okAdvisor = new AgentHelpLlmAdvisor(
            new SequenceLlmProvider(
                """{"role":"assistant","content":"customer add"}""",
                """{"role":"assistant","content":"Go to /Customer and click Add."}"""),
            catalog,
            NullLogger<AgentHelpLlmAdvisor>.Instance);

        var (summary, usage) = await okAdvisor.BuildAutoHelpContextAsync("Goal: How do I add a customer?\nDate: 2026-01-01", CancellationToken.None);
        Assert.Equal("Go to /Customer and click Add.", summary);
        Assert.Equal(2, usage.Calls);

        var errAdvisor = new AgentHelpLlmAdvisor(
            new SequenceLlmProvider("ERROR:down", "ERROR:down"),
            catalog,
            NullLogger<AgentHelpLlmAdvisor>.Instance);
        var (fallbackSummary, _) = await errAdvisor.BuildAutoHelpContextAsync("Customer", CancellationToken.None);
        Assert.Equal("Click Add on /Customer.", fallbackSummary);
    }

    [Fact]
    public async Task AgentPermissionGate_EnforcesContextualScopeDenyAndSemiAuto()
    {
        var gate = new AgentPermissionGate();
        var prompts = 0;
        Task<PermissionDecision> AlwaysAllow(string _, string __, CancellationToken ___)
        {
            prompts++;
            return Task.FromResult(PermissionDecision.AllowAlways);
        }

        // Manual mode: AllowAlways caches for exact route|inDialog|text
        var (ok1, _) = await gate.AuthorizeAsync(AgentMode.Manual, "/Customer", false, "Add", "save", "", "{}", AlwaysAllow, CancellationToken.None);
        var (ok2, _) = await gate.AuthorizeAsync(AgentMode.Manual, "/Customer", false, "Add", "save", "", "{}", AlwaysAllow, CancellationToken.None);
        var (okDiffScope, _) = await gate.AuthorizeAsync(AgentMode.Manual, "/Customer", true, "Add", "save", "", "{}", AlwaysAllow, CancellationToken.None);
        Assert.True(ok1 && ok2 && okDiffScope);
        Assert.Equal(2, prompts);

        // Confirm always prompts even in Auto mode
        var (okConfirm, _) = await gate.AuthorizeAsync(AgentMode.Auto, "/Customer", true, "Yes", "confirm", "", "{}", AlwaysAllow, CancellationToken.None);
        Assert.True(okConfirm);
        Assert.Equal(3, prompts);

        // SemiAuto: read does not prompt, save prompts
        var (okRead, _) = await gate.AuthorizeAsync(AgentMode.SemiAuto, "/Lead", false, "Filter", "read", "", "{}", AlwaysAllow, CancellationToken.None);
        Assert.True(okRead);
        Assert.Equal(3, prompts);

        // Deny blocks immediately and remembers denial for identical context
        var denyPrompts = 0;
        Task<PermissionDecision> DenyOnce(string _, string __, CancellationToken ___)
        {
            denyPrompts++;
            return Task.FromResult(PermissionDecision.Deny);
        }
        var (denied1, reason1) = await gate.AuthorizeAsync(AgentMode.Manual, "/Lead", false, "Save", "save", "", "{}", DenyOnce, CancellationToken.None);
        var (denied2, reason2) = await gate.AuthorizeAsync(AgentMode.Manual, "/Lead", false, "Save", "save", "", "{}", DenyOnce, CancellationToken.None);
        Assert.False(denied1);
        Assert.False(denied2);
        Assert.Equal(1, denyPrompts);
        Assert.Contains("declined", reason1);
        Assert.Contains("declined", reason2);

        // Named delete: first prompt on grid row approves target, second prompt inside confirmation dialog succeeds even if dialog omits row name
        var promptsBeforeDel = prompts;
        var (del1, _) = await gate.AuthorizeAsync(AgentMode.Auto, "/Customer", false, "Delete", "destructive", "Acme", """{"grids":[{"rows":[{"cells":["Acme"]}]}]}""", AlwaysAllow, CancellationToken.None);
        var (del2, _) = await gate.AuthorizeAsync(AgentMode.Auto, "/Customer", true, "Delete", "destructive", "Acme", """{"title":"Confirm Delete","inDialog":true,"fields":[],"grids":[]}""", AlwaysAllow, CancellationToken.None);
        var (delOther, _) = await gate.AuthorizeAsync(AgentMode.Auto, "/Customer", true, "Delete", "destructive", "Beta", """{"title":"Confirm Delete","inDialog":true,"fields":[],"grids":[]}""", AlwaysAllow, CancellationToken.None);
        var (delDialogText, _) = await gate.AuthorizeAsync(AgentMode.Auto, "/Customer", true, "Delete", "destructive", "Beta", """{"title":"Confirm","dialogText":"Delete Beta?","inDialog":true,"fields":[],"grids":[]}""", AlwaysAllow, CancellationToken.None);
        Assert.True(del1 && del2 && delDialogText);
        Assert.False(delOther);
        Assert.Equal(promptsBeforeDel + 3, prompts);
    }

    [Fact]
    public async Task AgentService_StepAsync_ExecutesServerHopBlocksRepeatsAndEnforcesRateLimitAndBudget()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new SequenceLlmProvider(
            """{"role":"assistant","content":null,"tool_calls":[{"id":"t1","type":"function","function":{"name":"echo_tool","arguments":"{\"x\":1}"}}]}""",
            """{"role":"assistant","content":null,"tool_calls":[{"id":"t2","type":"function","function":{"name":"echo_tool","arguments":"{\"x\":1}"}}]}""",
            """{"role":"assistant","content":"Done after repeat guard."}""");

        var handler = new EchoServerToolHandler();
        var service = new AgentService(
            provider,
            new MemoryCacheRateLimiter(cache),
            [handler],
            Options.Create(new AgentOptions { Enabled = true, MaxCallsPerUserPerMinute = 2, MaxTaskCostUsd = 0.50m }),
            NullLogger<AgentService>.Instance);

        var res = await service.StepAsync(new AgentStepRequestModel
        {
            Messages = ["""{"role":"user","content":"run echo twice"}"""]
        }, "u_multi", CancellationToken.None);

        Assert.Null(res.Error);
        Assert.Equal(1, handler.Executions); // 2nd identical call blocked by repeat guard
        Assert.Equal(4, res.Prior.Count); // 2 server hops * (assistant + tool)

        // Cost budget exceeded forces [final call] with tools: null
        var budgetProvider = new SequenceLlmProvider("""{"role":"assistant","content":"Budget summary"}""");
        var budgetService = new AgentService(
            budgetProvider,
            new MemoryCacheRateLimiter(cache),
            [handler],
            Options.Create(new AgentOptions { Enabled = true, MaxTaskCostUsd = 0.25m }),
            NullLogger<AgentService>.Instance);
        var budgetRes = await budgetService.StepAsync(new AgentStepRequestModel
        {
            Messages = ["""{"role":"user","content":"continue"}"""],
            CostSoFarUsd = 0.25m
        }, "u_budget", CancellationToken.None);
        Assert.Null(budgetRes.Error);
        Assert.Null(budgetProvider.LastTools);

        // Rate limit exceeded on 3rd call for u_multi
        await service.StepAsync(new AgentStepRequestModel { Messages = ["""{"role":"user","content":"2nd"}"""] }, "u_multi", CancellationToken.None);
        var limited = await service.StepAsync(new AgentStepRequestModel { Messages = ["""{"role":"user","content":"3rd"}"""] }, "u_multi", CancellationToken.None);
        Assert.Contains("Too many agent requests", limited.Error);
    }

    [Fact]
    public async Task AgentController_ReturnsNotFoundWhenDisabled()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var opts = Options.Create(new AgentOptions { Enabled = false });
        var service = new AgentService(
            new SequenceLlmProvider("""{"role":"assistant","content":"ok"}"""),
            new MemoryCacheRateLimiter(cache),
            [],
            opts,
            NullLogger<AgentService>.Instance);

        var controller = new AgentController(service, opts)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u1")])) }
            }
        };

        var status = controller.Status().Result as OkObjectResult;
        Assert.NotNull(status);
        var statusModel = Assert.IsType<AgentStatusModel>(status!.Value);
        Assert.False(statusModel.UseAgent);

        var stepResult = await controller.StepAsync(new AgentStepRequestModel { Messages = ["""{"role":"user","content":"hi"}"""] }, CancellationToken.None);
        Assert.IsType<NotFoundResult>(stepResult.Result);
    }

    private sealed class FakeHostEnv(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubHelpCatalog(List<HelpSearchHit> hits) : IAgentHelpCatalog
    {
        public List<HelpSearchHit> Search(string query, int maxResults = 3) => hits.Take(maxResults).ToList();
        public HelpSearchHit? GetPage(string titleQuery) => hits.FirstOrDefault();
    }

    private sealed class EchoServerToolHandler : IServerToolHandler
    {
        public string Name => "echo_tool";
        public int Executions { get; private set; }
        public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            Executions++;
            return Task.FromResult("""{"ok":true,"echo":1}""");
        }
    }

    private sealed class SequenceLlmProvider(params string[] responses) : IAgentLlmProvider
    {
        private int _index;
        public JsonElement[]? LastTools { get; private set; }

        public Task<LlmCallResult> CompleteAsync(
            IReadOnlyList<JsonObject> messages,
            JsonElement[]? tools,
            CancellationToken cancellationToken)
        {
            LastTools = tools;
            var payload = _index < responses.Length ? responses[_index++] : responses[^1];
            if (payload.StartsWith("ERROR:", StringComparison.Ordinal))
                return Task.FromResult(new LlmCallResult(null, new AgentUsageModel { Calls = 1 }, payload[6..]));

            return Task.FromResult(new LlmCallResult(payload, new AgentUsageModel { Calls = 1, InputTokens = 10, OutputTokens = 5 }, null));
        }
    }
}
