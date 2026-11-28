using MudBlazorCrmApp.Shared.Blazor.Services;
using MudBlazorCrmApp.Shared.Blazor.Services.Agent;
using Xunit;

namespace MudBlazorCrmApp.Tests;

/// <summary>
/// Unit tests for the agent log display pipeline (SLWEB parity port):
/// grouping, step titles, working mode and the copy-chat text.
/// </summary>
public class AgentLogViewTests
{
    private static readonly AppText L = new();

    private static AgentClientService.AgentChatEntry Entry(string kind, string text, bool blocked = false, string? sub = null)
        => new(kind, text, blocked, sub);

    [Fact]
    public void Build_FoldsConsecutiveToolSteps_IntoOneGroup()
    {
        var entries = new[]
        {
            Entry("user", "مشتری جدید بساز", sub: "task"),
            Entry("tool", "go_to_page({\"query\":\"Customers\"})"),
            Entry("tool", "click_action({\"id\":\"button:افزودن\"})"),
            Entry("assistant", "مشتری ساخته شد.")
        };

        var views = AgentLogViewBuilder.Build(entries, L);

        Assert.Collection(views,
            v => Assert.IsType<AgentTaskView>(v),
            v =>
            {
                var group = Assert.IsType<AgentStepGroupView>(v);
                Assert.Equal(2, group.StepCount);
                Assert.False(group.HasWarning);
            },
            v =>
            {
                var card = Assert.IsType<AgentCardView>(v);
                Assert.Equal("done", card.CardType);
            });
    }

    [Fact]
    public void Build_BlockedStep_MarksGroupWarning_AndAnswersRenderAsUserLines()
    {
        var entries = new[]
        {
            Entry("tool", "click_action({\"id\":\"button:حذف\"})", blocked: true),
            Entry("user", "ادامه بده", sub: "answer")
        };

        var views = AgentLogViewBuilder.Build(entries, L);

        var group = Assert.IsType<AgentStepGroupView>(views[0]);
        Assert.True(group.HasWarning);
        var line = Assert.IsType<AgentLineView>(views[1]);
        Assert.Equal("user", line.Tone);
    }

    [Fact]
    public void Build_CardTypes_ForQuestionAndError()
    {
        var entries = new[]
        {
            Entry("question", "کدام مشتری؟"),
            Entry("error", "timeout")
        };
        var views = AgentLogViewBuilder.Build(entries, L);

        Assert.Equal("ask", Assert.IsType<AgentCardView>(views[0]).CardType);
        Assert.Equal("error", Assert.IsType<AgentCardView>(views[1]).CardType);
    }

    [Fact]
    public void StepTitle_UsesCaptionsAndClips()
    {
        var title = AgentLogViewBuilder.StepTitle("go_to_page({\"query\":\"Customers\"})", L);
        Assert.Contains("Customers", title);

        var unknown = AgentLogViewBuilder.StepTitle("mystery_tool({})", L);
        Assert.Equal("mystery_tool", unknown);
    }

    [Fact]
    public void CurrentMode_MapsFromNewestTool_AndAskWhenWaiting()
    {
        var entries = new[] { Entry("tool", "set_field({\"id\":\"name\",\"value\":\"x\"})") };
        Assert.Equal(AgentWorkingMode.Write, AgentLogViewBuilder.CurrentMode(entries, false));

        Assert.Equal(AgentWorkingMode.Ask, AgentLogViewBuilder.CurrentMode(entries, true));
        Assert.Equal(AgentWorkingMode.Think, AgentLogViewBuilder.CurrentMode([], false));
    }

    [Fact]
    public void ChatText_IncludesTaskAndSteps_ForCopyChat()
    {
        var entries = new[]
        {
            Entry("user", "گزارش بده", sub: "task"),
            Entry("tool", "read_data({\"name\":\"dashboard_kpis\"})"),
            Entry("assistant", "بفرمایید.")
        };
        var text = AgentLogViewBuilder.ChatText(entries, L);

        Assert.Contains("گزارش بده", text);
        Assert.Contains("dashboard_kpis", text);
        Assert.Contains("بفرمایید", text);
    }

    [Fact]
    public void DecodeToolText_UnescapesPersianArgs_KeepsPlainText()
    {
        var escaped = @"go_to_page({""name"":""\u0634\u0631\u06a9\u062a\u200c\u0647\u0627""})";
        var decoded = AgentLogViewBuilder.DecodeToolText(escaped);
        Assert.Contains("شرکت‌ها", decoded);
        Assert.DoesNotContain("\\u", decoded);
        Assert.StartsWith("go_to_page(", decoded);

        Assert.Equal("click_action({\"id\":\"a1\"})", AgentLogViewBuilder.DecodeToolText("click_action({\"id\":\"a1\"})"));
        Assert.Equal(@"not json (\u0041", AgentLogViewBuilder.DecodeToolText(@"not json (\u0041"));

        var doubleEscaped = "finish({\"summary\":\"سلام! \\\\uD83D\\\\uDC4B خوش\"})";
        Assert.Contains("👋", AgentLogViewBuilder.DecodeToolText(doubleEscaped));
    }
}
