using MudBlazorCrmApp.Shared.Blazor.Services.Agent;
using Xunit;

namespace MudBlazorCrmApp.Tests;

public class AgentTaskOutcomeMemoryTests
{
    [Fact]
    public void Block_EmptyMemory_ReturnsNothingToInject()
    {
        Assert.Equal(string.Empty, AgentTaskOutcomeMemory.Block([]));
    }

    [Fact]
    public void Block_InjectsOnlyTheLastSixInOrderWithReasons()
    {
        var items = new List<AgentTaskOutcome>();
        for (var i = 1; i <= 8; i++)
            AgentTaskOutcomeMemory.Add(items, $"goal {i}", i == 8 ? AgentTaskOutcomeMemory.Blocked : AgentTaskOutcomeMemory.Done, i == 8 ? "budget reached" : string.Empty);

        var block = AgentTaskOutcomeMemory.Block(items);

        Assert.StartsWith("Recent outcomes:\n", block, StringComparison.Ordinal);
        Assert.DoesNotContain("goal 2 ", block, StringComparison.Ordinal);
        Assert.Contains("- goal 3 -> done", block, StringComparison.Ordinal);
        Assert.Contains("- goal 8 -> blocked (budget reached)", block, StringComparison.Ordinal);
        Assert.Equal(AgentTaskOutcomeMemory.MaxInjected + 1, block.Split('\n').Length);
    }

    [Fact]
    public void Add_CapsStoredItemsAndCollapsesWhitespaceInGoal()
    {
        var items = new List<AgentTaskOutcome>();
        for (var i = 0; i < AgentTaskOutcomeMemory.MaxStored + 5; i++)
            AgentTaskOutcomeMemory.Add(items, $"first line {i}\nsecond   line", AgentTaskOutcomeMemory.Done, string.Empty);

        Assert.Equal(AgentTaskOutcomeMemory.MaxStored, items.Count);
        Assert.Equal("first line 5 second line", items[0].Goal);
    }

    [Fact]
    public void Parse_RoundTripsSerializedMemory()
    {
        var items = new List<AgentTaskOutcome>();
        AgentTaskOutcomeMemory.Add(items, "Find ACME", AgentTaskOutcomeMemory.Error, "provider timeout");

        var parsed = AgentTaskOutcomeMemory.Parse(AgentTaskOutcomeMemory.Serialize(items));

        Assert.Single(parsed);
        Assert.Equal(items[0], parsed[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"unexpected\":true}")]
    public void Parse_BadOrEmptyInputYieldsEmptyMemory(string? json)
    {
        Assert.Empty(AgentTaskOutcomeMemory.Parse(json));
    }
}
