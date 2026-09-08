using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;
using Xunit;

namespace CodeSpace.UnitTests.Workflows;

public sealed class MapTerminalOutcomeTests
{
    [Fact]
    public void No_completed_map_preserves_a_clean_success() => Assert(Array.Empty<MapCompletionFacts>(), WorkflowRunStatus.Success, null, null);

    [Fact]
    public void An_empty_or_clean_map_preserves_a_clean_success() => Assert(new[] { new MapCompletionFacts("empty", 0, 0), new MapCompletionFacts("clean", 3, 0) }, WorkflowRunStatus.Success, null, null);

    [Fact]
    public void Some_failed_branches_keep_the_delivery_but_mark_it_partial() => Assert(new[] { new MapCompletionFacts("fanout", 3, 1) }, WorkflowRunStatus.Success, WorkflowRunOutcomes.PartialFailure, null);

    [Fact]
    public void Every_branch_failing_turns_structural_completion_into_failure() => Assert(new[] { new MapCompletionFacts("fanout", 2, 2) }, WorkflowRunStatus.Failure, WorkflowRunOutcomes.AllBranchesFailed, "All 2 branches failed in map 'fanout'.");

    [Theory]
    [InlineData(null, 0)]
    [InlineData(1, null)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2)]
    public void Invalid_engine_counters_fail_closed(int? count, int? failed) => Assert(new[] { new MapCompletionFacts("fanout", count, failed) }, WorkflowRunStatus.Failure, null, "Map 'fanout' reported invalid completion counters.");

    private static void Assert(IReadOnlyList<MapCompletionFacts> maps, WorkflowRunStatus status, string? outcome, string? error)
    {
        var actual = MapTerminalOutcome.Classify(maps);

        actual.Status.ShouldBe(status);
        actual.Outcome.ShouldBe(outcome);
        actual.Error.ShouldBe(error);
    }
}
