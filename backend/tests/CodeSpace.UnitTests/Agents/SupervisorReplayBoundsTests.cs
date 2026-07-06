using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// A REPLAY / RERUN of a supervisor run must NOT be strangled by the round / total-spawn ceilings frozen into the
/// original run's snapshot (a stale tier value from before "loop-until-done"). <see cref="AgentSupervisorNode.RelaxBoundsForReplay"/>
/// drops those two ceilings on a replay/rerun source so the plan falls to the CURRENT back-stop defaults, while a fresh
/// (manual/api/schedule) launch keeps an operator's explicit pin. MaxParallelism / cost / no-progress are untouched.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorReplayBoundsTests
{
    private static SupervisorGoalConfig Frozen() => new() { MaxRounds = 6, MaxTotalSpawns = 20, MaxParallelism = 5, MaxCostUsd = 12.5m, MaxNoProgressDecisions = 8 };

    [Theory]
    [InlineData(WorkflowRunSourceTypes.Replay)]
    [InlineData(WorkflowRunSourceTypes.Rerun)]
    public void A_replay_or_rerun_drops_the_frozen_round_and_total_spawn_ceilings(string sourceType)
    {
        var relaxed = AgentSupervisorNode.RelaxBoundsForReplay(Frozen(), sourceType);

        relaxed!.MaxRounds.ShouldBeNull("a rerun loops until done — it must not inherit the frozen round budget");
        relaxed.MaxTotalSpawns.ShouldBeNull("the frozen total-spawn ceiling is dropped too");
        relaxed.MaxParallelism.ShouldBe(5, "concurrency is a resource knob — kept verbatim");
        relaxed.MaxCostUsd.ShouldBe(12.5m, "the cost budget is kept — it's the real loop-until-done bound");
        relaxed.MaxNoProgressDecisions.ShouldBe(8, "the no-progress guard is kept");
    }

    [Theory]
    [InlineData(WorkflowRunSourceTypes.Manual)]
    [InlineData(WorkflowRunSourceTypes.Api)]
    [InlineData("")]
    public void A_fresh_launch_keeps_an_explicit_pin(string sourceType)
    {
        var kept = AgentSupervisorNode.RelaxBoundsForReplay(Frozen(), sourceType);

        kept!.MaxRounds.ShouldBe(6, "a non-replay run honors an operator's explicit maxRounds pin");
        kept.MaxTotalSpawns.ShouldBe(20);
    }

    [Fact]
    public void A_null_config_stays_null()
    {
        AgentSupervisorNode.RelaxBoundsForReplay(null, WorkflowRunSourceTypes.Replay).ShouldBeNull();
    }
}
