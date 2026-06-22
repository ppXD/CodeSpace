using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Always-on (no model, no Postgres) teeth for the multi-turn trajectory harness + scorer (A3): drive SCRIPTED
/// deciders through the simulated happy-path environment and prove the scorer (a) PASSES a brain that drives to
/// completion (plan→spawn→merge→stop) and (b) FAILS a brain that loops forever or quits empty. This is the harness
/// the real-model trajectory gate scores against — pinning it here means the live gate measures the BRAIN, not a
/// broken scorer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SupervisorTrajectoryEvalTests
{
    [Fact]
    public async Task A_brain_that_drives_plan_spawn_merge_stop_scores_ok()
    {
        var result = await SupervisorTrajectory.RunAsync(new ConvergingDecider(), maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue(note);
    }

    [Fact]
    public async Task A_brain_that_loops_replanning_hits_the_cap_and_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new AlwaysPlanDecider(), maxTurns: 6, CancellationToken.None);

        result.ReachedStop.ShouldBeFalse("a never-stopping brain hits the turn cap");
        SupervisorTrajectoryScore.Score(result).Ok.ShouldBeFalse("looping forever must score a failure — the whole point of a trajectory measure");
    }

    [Fact]
    public async Task A_brain_that_stops_immediately_without_shipping_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new StopImmediatelyDecider(), maxTurns: 6, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it did stop — but with nothing shipped");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("quitting before any merge/resolve must fail — a stop is only good after shipping");
        note.ShouldContain("WITHOUT shipping");
    }

    // ── Scripted deciders (decide purely from the prior-decision kinds — no model) ──────────────────────────

    /// <summary>A converging brain: plan if nothing planned, spawn if planned-not-spawned, merge if spawned-not-merged, else stop.</summary>
    private sealed class ConvergingDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = kind == SupervisorDecisionKinds.Stop ? "{\"outcome\":\"completed\"}" : "{}" });
        }
    }

    private sealed class AlwaysPlanDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = "{}" });
    }

    private sealed class StopImmediatelyDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision { Kind = SupervisorDecisionKinds.Stop, PayloadJson = "{\"outcome\":\"completed\"}" });
    }
}
