using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// 🟢 Unit: P19's single source of truth mapping a TaskLaunch arm onto the effort the real Launch entry is asked
/// for. Pinned so a future rename of a mode or an effort constant surfaces here first, not as a silently wrong
/// launch.
/// </summary>
[Trait("Category", "Unit")]
public class BenchmarkModeEffortTests
{
    [Theory]
    [InlineData(BenchmarkMode.TaskLaunchQuick, true)]
    [InlineData(BenchmarkMode.TaskLaunchStandard, true)]
    [InlineData(BenchmarkMode.TaskLaunchDeep, true)]
    [InlineData(BenchmarkMode.TaskLaunchAuto, true)]
    [InlineData(BenchmarkMode.HarnessCli, false)]
    [InlineData(BenchmarkMode.HarnessCliWithMcp, false)]
    [InlineData(BenchmarkMode.WorkflowMap, false)]
    public void IsTaskLaunch_is_true_only_for_the_four_launch_arms(BenchmarkMode mode, bool expected)
    {
        BenchmarkModeEffort.IsTaskLaunch(mode).ShouldBe(expected);
    }

    [Theory]
    [InlineData(BenchmarkMode.TaskLaunchQuick, TaskEffortModes.Quick)]
    [InlineData(BenchmarkMode.TaskLaunchStandard, TaskEffortModes.Standard)]
    [InlineData(BenchmarkMode.TaskLaunchDeep, TaskEffortModes.Deep)]
    public void RequestedEffortFor_names_the_arms_concrete_effort_tier(BenchmarkMode mode, string expectedEffort)
    {
        BenchmarkModeEffort.RequestedEffortFor(mode).ShouldBe(expectedEffort);
    }

    [Fact]
    public void RequestedEffortFor_auto_is_null_not_the_literal_string_auto()
    {
        // Null is the router's own "classify it" contract (TaskLaunchRequest.RequestedEffort); the literal
        // string "auto" would ALSO work today, but null is what every other Launch caller sends.
        BenchmarkModeEffort.RequestedEffortFor(BenchmarkMode.TaskLaunchAuto).ShouldBeNull();
    }

    [Fact]
    public void RequestedEffortFor_throws_for_a_non_launch_mode()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => BenchmarkModeEffort.RequestedEffortFor(BenchmarkMode.HarnessCli));
    }
}
