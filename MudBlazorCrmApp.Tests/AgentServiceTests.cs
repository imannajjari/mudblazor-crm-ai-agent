using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MudBlazorCrmApp.Data;
using MudBlazorCrmApp.Models;
using MudBlazorCrmApp.Services.Agent;
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
}
