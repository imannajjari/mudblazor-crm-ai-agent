using MudBlazorCrmApp.Models;
using MudBlazorCrmApp.Services.Agent;
using MudBlazorCrmApp.Shared.Blazor.Services.Agent;
using Xunit;

namespace MudBlazorCrmApp.Tests;

public class AgentPhase7KnowledgeTests
{
    [Fact]
    public void LearningNudge_FiresOnceForHardTaskWithoutKnowledgeWrites()
    {
        var tracker = new AgentTaskRepeatTracker();
        for (var i = 0; i < AgentTaskRepeatTracker.LearningNudgeMinActions - 1; i++)
        {
            tracker.MarkAction("set_field");
            Assert.False(tracker.ShouldNudgeLearning());
        }

        tracker.MarkAction("set_field");
        tracker.MarkAction("click_action");
        Assert.True(tracker.ShouldNudgeLearning());
        Assert.True(tracker.LearningNudged);
        Assert.False(tracker.ShouldNudgeLearning()); // never a second nudge
    }

    [Fact]
    public void LearningNudge_SuppressedByKnowledgeWritesAndShortTasks()
    {
        var tracker = new AgentTaskRepeatTracker();
        tracker.MarkAction("remember");
        for (var i = 0; i < AgentTaskRepeatTracker.LearningNudgeMinActions; i++)
            tracker.MarkAction("set_field");
        Assert.False(tracker.ShouldNudgeLearning());

        var shortTask = new AgentTaskRepeatTracker();
        for (var i = 0; i < AgentTaskRepeatTracker.LearningNudgeMinActions - 1; i++)
            shortTask.MarkAction("set_field");
        Assert.False(shortTask.ShouldNudgeLearning());
    }

    [Fact]
    public void LearningNudge_CorrectFactCountsAndFinishAskUserAreNotActions()
    {
        var tracker = new AgentTaskRepeatTracker();
        tracker.MarkAction("correct_fact");
        Assert.False(tracker.ShouldNudgeLearning());

        var chatter = new AgentTaskRepeatTracker();
        for (var i = 0; i < AgentTaskRepeatTracker.LearningNudgeMinActions; i++)
        {
            chatter.MarkAction("finish");
            chatter.MarkAction("ask_user");
        }
        Assert.False(chatter.ShouldNudgeLearning());
    }

    [Fact]
    public void LearningNudge_SuppressedWhenLoopGuardForcedTheFinish()
    {
        var tracker = new AgentTaskRepeatTracker();
        for (var i = 0; i < AgentTaskRepeatTracker.LearningNudgeMinActions; i++)
            tracker.MarkAction("set_field");
        tracker.MarkLoopBlock();
        tracker.MarkLoopBlock();
        Assert.False(tracker.ShouldNudgeLearning());
    }

    [Fact]
    public void Purger_OlderThan_DeletesOnlyStaleAndLittleUsedFacts()
    {
        var now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        var old = now.AddDays(-120);
        var shouldPurge = AgentKnowledgePurger.OlderThan(AgentKnowledgePurger.Cutoff(now)).Compile();

        Assert.True(shouldPurge(Row(created: old.AddDays(-10)))); // created long ago, never used
        Assert.True(shouldPurge(Row(created: old, modified: old, lastUsed: old, useCount: 9))); // old but used long ago
        Assert.False(shouldPurge(Row(created: old, modified: now.AddDays(-5)))); // touched recently
        Assert.False(shouldPurge(Row(created: old, lastUsed: now.AddDays(-5), useCount: 3))); // used recently and enough
        Assert.True(shouldPurge(Row(created: old, lastUsed: now.AddDays(-5), useCount: 2))); // used recently but never earned use
    }

    [Fact]
    public void Purger_RetentionThresholds_HaveExpectedValues()
    {
        Assert.Equal(2, AgentKnowledgePurger.RetireAfterNotHelped);
        Assert.Equal(90, AgentKnowledgePurger.RetainDays);
        Assert.Equal(3, AgentKnowledgePurger.MinUseCountToKeep);
    }

    private static AgentKnowledge Row(
        DateTime created, DateTime? modified = null, DateTime? lastUsed = null, int useCount = 0) =>
        new()
        {
            Topic = "t",
            Fact = "f",
            FactHash = "h",
            CreatedBy = "u",
            CreatedDate = created,
            ModifiedDate = modified,
            LastUsedDate = lastUsed,
            UseCount = useCount
        };
}
