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

    [Fact]
    public async Task A_brain_that_merges_without_planning_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new MergeFirstDecider(), maxTurns: 6, CancellationToken.None);

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("merging with nothing planned is shipping out of nothing — not driving to completion");
        note.ShouldContain("PLANNING");
    }

    [Fact]
    public async Task A_brain_that_plans_then_merges_without_doing_the_work_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new PlanThenMergeDecider(), maxTurns: 6, CancellationToken.None);

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("planning then merging with no spawn/retry/resolve is shipping out of nothing");
        note.ShouldContain("DOING THE WORK");
    }

    [Fact]
    public async Task A_brain_that_ships_then_churns_on_re_spawns_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new ChurningDecider(), maxTurns: 12, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it does eventually stop — but only after wasteful re-spawning");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("staging work far more than the happy path needs is non-converging churn");
        note.ShouldContain("churning");
    }

    [Fact]
    public async Task A_brain_that_asks_one_question_then_converges_passes()
    {
        var result = await SupervisorTrajectory.RunAsync(new AskThenConvergeDecider(), maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.AskHuman, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"a single legitimate ask_human on the happy path — answered, then converged — must NOT fail the gate ({note})");
    }

    [Fact]
    public async Task A_deadline_already_elapsed_scores_a_budget_failure_not_a_loop()
    {
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();   // a wall-clock deadline that already fired before the first turn

        var result = await SupervisorTrajectory.RunAsync(new AlwaysPlanDecider(), maxTurns: 6, deadline.Token);

        result.ReachedStop.ShouldBeFalse();
        result.HitTurnCap.ShouldBeFalse("a deadline cancellation is NOT a turn-cap loop — the scorer must name the two differently");
        SupervisorTrajectoryScore.Score(result).Note.ShouldContain("time budget");
    }

    [Fact]
    public async Task A_deadline_firing_mid_decision_is_caught_and_scored_not_thrown()
    {
        using var deadline = new CancellationTokenSource();

        // The decider cancels the deadline then throws OperationCanceledException — exactly a wall-clock deadline firing
        // while an HTTP call is in flight. RunAsync must convert that into a clean scored failure, never let it propagate.
        var result = await SupervisorTrajectory.RunAsync(new DeadlineThrowingDecider(deadline), maxTurns: 6, deadline.Token);

        result.ReachedStop.ShouldBeFalse();
        result.HitTurnCap.ShouldBeFalse();
        SupervisorTrajectoryScore.Score(result).Note.ShouldContain("time budget");
    }

    [Fact]
    public async Task A_per_call_HTTP_timeout_propagates_and_is_NOT_mislabeled_as_a_turn_cap()
    {
        // A per-call HttpClient.Timeout throws OperationCanceledException whose token is NOT the trajectory deadline
        // (the deadline never fired). RunAsync must let it propagate — swallowing it would mislabel a slow endpoint as a
        // looping model that "hit the turn cap" (the exact bug the real-model OpenAI wire surfaced).
        await Should.ThrowAsync<OperationCanceledException>(() =>
            SupervisorTrajectory.RunAsync(new PerCallTimeoutDecider(), maxTurns: 6, CancellationToken.None));
    }

    // ── Recovery environments: the brain must RESOLVE a conflict / RETRY a failure before it can ship ────────

    [Fact]
    public async Task A_brain_that_resolves_a_merge_conflict_then_ships_scores_ok()
    {
        var result = await SupervisorTrajectory.RunAsync(new ConflictResolvingDecider(), SupervisorTrajectoryEnvironments.ConflictThenResolve, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Resolve, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"resolving the conflict to a VERIFIED resolution and stopping is a sound recovery ({note})");
    }

    [Fact]
    public async Task A_brain_that_gives_up_on_a_merge_conflict_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new ShipNaivelyDecider(), SupervisorTrajectoryEnvironments.ConflictThenResolve, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it stops — but on an unresolved conflict");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("stopping on a CONFLICTED merge ships nothing — the conflict was never resolved");
        note.ShouldContain("WITHOUT shipping");
    }

    [Fact]
    public async Task A_brain_that_retries_a_failed_agent_then_ships_scores_ok()
    {
        var result = await SupervisorTrajectory.RunAsync(new FailureRetryingDecider(), SupervisorTrajectoryEnvironments.FailureThenRetry, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"retrying the failed subtask then merging clean is a sound recovery ({note})");
    }

    [Fact]
    public async Task A_brain_that_merges_over_a_failed_agent_without_retrying_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new ShipNaivelyDecider(), SupervisorTrajectoryEnvironments.FailureThenRetry, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it stops — but the merge was incomplete (a subtask never succeeded)");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("merging over an un-retried failure integrates nothing clean — it ships no reviewable head");
        note.ShouldContain("WITHOUT shipping");
    }

    // ── Complex multi-turn recovery: persist past an unverified resolution / recover from multiple failures ───

    [Fact]
    public async Task A_brain_that_persists_past_an_unverified_resolution_and_ships_scores_ok()
    {
        var result = await SupervisorTrajectory.RunAsync(new ConflictPersistingDecider(), SupervisorTrajectoryEnvironments.ConflictThenUnverifiedThenVerified, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Resolve, SupervisorDecisionKinds.Resolve, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"re-resolving past the unverified reconciliation to a VERIFIED one and stopping is a sound recovery ({note})");
    }

    [Fact]
    public async Task A_brain_that_stops_on_the_first_unverified_resolution_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new ResolveOnceThenStopDecider(), SupervisorTrajectoryEnvironments.ConflictThenUnverifiedThenVerified, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it stops — but on an UNVERIFIED resolution");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("accepting the first unverified resolution ships nothing — the reconciliation never passed the build/tests");
        note.ShouldContain("WITHOUT shipping");
    }

    [Fact]
    public async Task A_brain_that_retries_every_failure_then_ships_scores_ok()
    {
        var result = await SupervisorTrajectory.RunAsync(new MultiFailureRecoveringDecider(), SupervisorTrajectoryEnvironments.MultiFailureThenRetry, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"retrying BOTH failed subtasks then merging clean is a sound multi-failure recovery ({note})");
    }

    [Fact]
    public async Task A_brain_that_merges_before_recovering_all_failures_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new RetryOnceThenMergeDecider(), SupervisorTrajectoryEnvironments.MultiFailureThenRetry, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it stops — but merged while a subtask was still unrecovered");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("merging before BOTH failures are retried integrates nothing clean — it ships no reviewable head");
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

    /// <summary>Ships out of nothing: merge as the first verb, then stop — no plan, no work.</summary>
    private sealed class MergeFirstDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind = !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = "{}" });
        }
    }

    /// <summary>Plans then merges with no spawn/retry/resolve in between — shipping out of nothing despite a plan.</summary>
    private sealed class PlanThenMergeDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = "{}" });
        }
    }

    /// <summary>Ships, but only after staging work far more than the happy path needs (5 spawns) — non-converging churn.</summary>
    private sealed class ChurningDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : kinds.Count(k => k == SupervisorDecisionKinds.Spawn) < 5 ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = "{}" });
        }
    }

    /// <summary>A cautious-but-correct brain: plan, ask ONE question, then spawn → merge → stop. The ask is answered (the harness folds a real reply) and the scorer must tolerate the detour.</summary>
    private sealed class AskThenConvergeDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.AskHuman) ? SupervisorDecisionKinds.AskHuman
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = "{}" });
        }
    }

    /// <summary>Simulates a wall-clock deadline firing mid-decision: cancels the deadline source, then throws — RunAsync must catch it and score a budget failure, not propagate.</summary>
    private sealed class DeadlineThrowingDecider : ISupervisorDecider
    {
        private readonly CancellationTokenSource _deadline;
        public DeadlineThrowingDecider(CancellationTokenSource deadline) { _deadline = deadline; }

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            _deadline.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>Simulates an HttpClient per-call timeout: throws OperationCanceledException WITHOUT the trajectory deadline being cancelled — RunAsync must let it propagate, not swallow it as a turn-cap.</summary>
    private sealed class PerCallTimeoutDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    /// <summary>A conflict-aware brain: plan→spawn→merge, and when that merge CONFLICTS, spawn a resolver (→verified) and stop on the accepted resolution. Reads the ledger outcomes, not just kinds.</summary>
    private sealed class ConflictResolvingDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var priors = context.PriorDecisions;
            var kinds = priors.Select(d => d.DecisionKind).ToList();
            var conflicted = priors.Any(d => d.DecisionKind == SupervisorDecisionKinds.Merge && SupervisorOutcome.ReadIntegration(d.OutcomeJson) is { IsConflicted: true });
            var verifiedResolve = priors.Any(d => d.DecisionKind == SupervisorDecisionKinds.Resolve && SupervisorOutcome.ReadResolutionVerdict(d.OutcomeJson) == SupervisorResolutionVerdict.Verified);

            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : conflicted && !verifiedResolve ? SupervisorDecisionKinds.Resolve
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = "{}" });
        }
    }

    /// <summary>A failure-aware brain: plan→spawn, and when an agent FAILED, retry the failed subtask before merging clean and stopping. Reads the ledger's agent results.</summary>
    private sealed class FailureRetryingDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var priors = context.PriorDecisions;
            var kinds = priors.Select(d => d.DecisionKind).ToList();
            var hasFailure = priors.Any(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind) && SupervisorOutcome.ReadAgentResults(d.OutcomeJson).Any(r => string.Equals(r.Status, "Failed", StringComparison.OrdinalIgnoreCase)));

            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : hasFailure && !kinds.Contains(SupervisorDecisionKinds.Retry) ? SupervisorDecisionKinds.Retry
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = "{}" });
        }
    }

    /// <summary>A naive brain that ignores recovery signals: plan→spawn→merge→stop regardless of conflict/failure. In a recovery environment its merge never integrates cleanly, so it ships nothing — the scorer must fail it.</summary>
    private sealed class ShipNaivelyDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = "{}" });
        }
    }

    /// <summary>A persistent brain: plan→spawn→merge, and while the merge is CONFLICTED and no VERIFIED resolve exists, keep resolving (so it re-resolves past an unverified reconciliation) — then stop on the accepted, verified resolution.</summary>
    private sealed class ConflictPersistingDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var priors = context.PriorDecisions;
            var kinds = priors.Select(d => d.DecisionKind).ToList();
            var conflicted = priors.Any(d => d.DecisionKind == SupervisorDecisionKinds.Merge && SupervisorOutcome.ReadIntegration(d.OutcomeJson) is { IsConflicted: true });
            var verifiedResolve = priors.Any(d => d.DecisionKind == SupervisorDecisionKinds.Resolve && SupervisorOutcome.ReadResolutionVerdict(d.OutcomeJson) == SupervisorResolutionVerdict.Verified);

            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : conflicted && !verifiedResolve ? SupervisorDecisionKinds.Resolve
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = "{}" });
        }
    }

    /// <summary>A brain that ACCEPTS the first resolution: plan→spawn→merge→resolve(once)→stop. Against the unverified-then-verified environment its single resolve is unverified, so it ships nothing — the scorer must fail it.</summary>
    private sealed class ResolveOnceThenStopDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var priors = context.PriorDecisions;
            var kinds = priors.Select(d => d.DecisionKind).ToList();
            var conflicted = priors.Any(d => d.DecisionKind == SupervisorDecisionKinds.Merge && SupervisorOutcome.ReadIntegration(d.OutcomeJson) is { IsConflicted: true });

            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : conflicted && !kinds.Contains(SupervisorDecisionKinds.Resolve) ? SupervisorDecisionKinds.Resolve
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = "{}" });
        }
    }

    /// <summary>A thorough recovering brain: plan→spawn, then RETRY until two retries have happened (recovering both failed subtasks), then merge→stop.</summary>
    private sealed class MultiFailureRecoveringDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : kinds.Count(k => k == SupervisorDecisionKinds.Retry) < 2 ? SupervisorDecisionKinds.Retry
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = "{}" });
        }
    }

    /// <summary>A hasty brain that retries only ONCE then merges: plan→spawn→retry→merge→stop. Against the multi-failure environment one failure is still unrecovered at merge, so it integrates nothing clean — the scorer must fail it.</summary>
    private sealed class RetryOnceThenMergeDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : kinds.Count(k => k == SupervisorDecisionKinds.Retry) < 1 ? SupervisorDecisionKinds.Retry
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = "{}" });
        }
    }
}
