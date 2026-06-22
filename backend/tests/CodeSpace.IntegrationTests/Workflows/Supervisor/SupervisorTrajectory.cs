using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The MULTI-TURN trajectory harness (A3) — the honest follow-up to the single-decision golden eval. It drives a
/// supervisor decider TURN BY TURN over a SIMULATED environment: each decision's outcome is folded into the ledger and
/// fed back as the next turn's context, exactly as the real engine would, until the decider STOPS or a turn cap / a
/// wall-clock deadline is hit. This measures what a single decision cannot — does the brain DRIVE TO COMPLETION and
/// STOP AT THE RIGHT TIME (after a real shippable result), rather than loop forever, quit empty, or give up.
///
/// <para>The environment is pluggable (<see cref="ISupervisorTrajectoryEnvironment"/>): the SUCCESS path
/// (<see cref="SupervisorTrajectoryEnvironments.HappyPath"/>), plus two RECOVERY paths — a merge CONFLICT the brain must
/// resolve+verify before it can ship (<see cref="SupervisorTrajectoryEnvironments.ConflictThenResolve"/>) and an agent
/// FAILURE the brain must retry before it can ship (<see cref="SupervisorTrajectoryEnvironments.FailureThenRetry"/>). The
/// decider under test is swapped: a scripted one pins the harness + scorer (always-on), the REAL model proves its
/// trajectory judgment against a live endpoint (the real-model gate). Pure of Postgres — it folds contexts in memory via
/// the same <c>SupervisorOutcome</c> helpers the engine uses, so the context is what the brain really reads.</para>
/// </summary>
public static class SupervisorTrajectory
{
    private static readonly Guid Brain = SupervisorDecisionGoldenScenarios.BrainModelRowId;

    /// <summary>Drive <paramref name="decider"/> over the SUCCESS path (back-compat overload).</summary>
    public static Task<SupervisorTrajectoryResult> RunAsync(ISupervisorDecider decider, int maxTurns, CancellationToken cancellationToken) =>
        RunAsync(decider, SupervisorTrajectoryEnvironments.HappyPath, maxTurns, cancellationToken);

    /// <summary>Run <paramref name="decider"/> turn by turn over <paramref name="environment"/> until it stops, <paramref name="maxTurns"/> is reached, or <paramref name="cancellationToken"/> (a wall-clock deadline) cancels it. A cancellation is converted into a clean non-stop result (scored as a failure) rather than thrown, so a slow real-model endpoint surfaces a legible verdict instead of an opaque CI timeout. Returns the ordered decision kinds, whether a terminal stop was reached, and the folded ledger the scorer reads.</summary>
    public static async Task<SupervisorTrajectoryResult> RunAsync(ISupervisorDecider decider, ISupervisorTrajectoryEnvironment environment, int maxTurns, CancellationToken cancellationToken)
    {
        var priors = new List<SupervisorPriorDecision>();
        var kinds = new List<string>();

        for (var turn = 0; turn < maxTurns; turn++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var context = new SupervisorTurnContext { Goal = SupervisorDecisionGoldenScenarios.FixtureGoal, TurnNumber = turn, PriorDecisions = priors.ToList(), SupervisorModelId = Brain };

            SupervisorDecision decision;
            try
            {
                decision = await decider.DecideAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;   // OUR wall-clock deadline fired mid-decision — fall through to a clean non-stop result (a scored failure, never an opaque CI timeout). A per-call HttpClient timeout (an OperationCanceledException whose token is NOT ours) is deliberately NOT caught here — it must propagate honestly rather than masquerade as a turn-cap loop.
            }

            kinds.Add(decision.Kind);

            if (decision.IsTerminal) return new SupervisorTrajectoryResult { Kinds = kinds, ReachedStop = true, HitTurnCap = false, Ledger = priors };

            priors.Add(environment.Fold(decision, turn, priors));
        }

        // Exhausted the turn cap (the brain loops) OR a deadline cancelled it — HitTurnCap distinguishes the two so the
        // scorer names the failure precisely (a true loop vs. a slow run that never converged inside the time budget).
        return new SupervisorTrajectoryResult { Kinds = kinds, ReachedStop = false, HitTurnCap = !cancellationToken.IsCancellationRequested, Ledger = priors };
    }
}

/// <summary>An environment the trajectory harness drives the decider over: it folds the decided action into the durable-shape outcome the NEXT turn reads, given the ledger so far — the SAME <c>SupervisorOutcome</c> shapes the engine writes, so the decider reads exactly what it would in production.</summary>
public interface ISupervisorTrajectoryEnvironment
{
    SupervisorPriorDecision Fold(SupervisorDecision decision, long sequence, IReadOnlyList<SupervisorPriorDecision> priorsSoFar);
}

/// <summary>The trajectory environments: the SUCCESS path + the two RECOVERY paths (a merge conflict the brain must resolve, an agent failure the brain must retry). Stateless singletons — each reads only the ledger passed to <see cref="ISupervisorTrajectoryEnvironment.Fold"/>.</summary>
public static class SupervisorTrajectoryEnvironments
{
    /// <summary>The SUCCESS path: spawned/retried work succeeds, a merge is CLEAN, a resolve is VERIFIED — so a competent brain converges plan→spawn→merge→stop.</summary>
    public static ISupervisorTrajectoryEnvironment HappyPath { get; } = new HappyPathEnvironment();

    /// <summary>A merge CONFLICT recovery path: the first integration CONFLICTS; the only way to ship is to spawn a resolver and VERIFY it — so a competent brain converges plan→spawn→merge(conflict)→resolve→stop.</summary>
    public static ISupervisorTrajectoryEnvironment ConflictThenResolve { get; } = new ConflictThenResolveEnvironment();

    /// <summary>An agent FAILURE recovery path: the first spawn returns one Succeeded + one Failed agent; the only way to ship is to RETRY the failed subtask — so a competent brain converges plan→spawn→retry→merge→stop.</summary>
    public static ISupervisorTrajectoryEnvironment FailureThenRetry { get; } = new FailureThenRetryEnvironment();

    private sealed class HappyPathEnvironment : ISupervisorTrajectoryEnvironment
    {
        public SupervisorPriorDecision Fold(SupervisorDecision d, long seq, IReadOnlyList<SupervisorPriorDecision> priors) => d.Kind switch
        {
            var k when k == SupervisorDecisionKinds.Plan => TrajectoryOutcomes.Plan(d, seq),
            var k when k == SupervisorDecisionKinds.Spawn || k == SupervisorDecisionKinds.Retry => TrajectoryOutcomes.AllSucceeded(d, seq),
            var k when k == SupervisorDecisionKinds.Merge => TrajectoryOutcomes.CleanMerge(d, seq),
            var k when k == SupervisorDecisionKinds.Resolve => TrajectoryOutcomes.VerifiedResolve(d, seq),
            var k when k == SupervisorDecisionKinds.AskHuman => TrajectoryOutcomes.AnsweredAsk(d, seq),
            _ => TrajectoryOutcomes.Handled(d, seq),
        };
    }

    private sealed class ConflictThenResolveEnvironment : ISupervisorTrajectoryEnvironment
    {
        public SupervisorPriorDecision Fold(SupervisorDecision d, long seq, IReadOnlyList<SupervisorPriorDecision> priors) => d.Kind switch
        {
            var k when k == SupervisorDecisionKinds.Plan => TrajectoryOutcomes.Plan(d, seq),
            var k when k == SupervisorDecisionKinds.Spawn || k == SupervisorDecisionKinds.Retry => TrajectoryOutcomes.AllSucceeded(d, seq),
            // The first integration CONFLICTS; a re-merge becomes CLEAN only once a VERIFIED resolve exists — so the brain
            // must resolve+verify to ship, and the scorer's ledger ship-check (ReadFinalIntegratedBranch) enforces it.
            var k when k == SupervisorDecisionKinds.Merge => TrajectoryOutcomes.HasVerifiedResolve(priors) ? TrajectoryOutcomes.CleanMerge(d, seq) : TrajectoryOutcomes.ConflictedMerge(d, seq),
            var k when k == SupervisorDecisionKinds.Resolve => TrajectoryOutcomes.VerifiedResolve(d, seq),
            var k when k == SupervisorDecisionKinds.AskHuman => TrajectoryOutcomes.AnsweredAsk(d, seq),
            _ => TrajectoryOutcomes.Handled(d, seq),
        };
    }

    private sealed class FailureThenRetryEnvironment : ISupervisorTrajectoryEnvironment
    {
        public SupervisorPriorDecision Fold(SupervisorDecision d, long seq, IReadOnlyList<SupervisorPriorDecision> priors) => d.Kind switch
        {
            var k when k == SupervisorDecisionKinds.Plan => TrajectoryOutcomes.Plan(d, seq),
            // The first spawn fails one subtask; a RETRY re-runs it and succeeds — so the brain must inspect the failure
            // and retry to ship.
            var k when k == SupervisorDecisionKinds.Spawn => TrajectoryOutcomes.OneFailed(d, seq),
            var k when k == SupervisorDecisionKinds.Retry => TrajectoryOutcomes.AllSucceeded(d, seq),
            // Integration is CLEAN only once the failure has been retried; a premature merge is INCOMPLETE (no branch),
            // so the ledger ship-check fails until the brain retries.
            var k when k == SupervisorDecisionKinds.Merge => TrajectoryOutcomes.HasRetry(priors) ? TrajectoryOutcomes.CleanMerge(d, seq) : TrajectoryOutcomes.IncompleteMerge(d, seq),
            var k when k == SupervisorDecisionKinds.Resolve => TrajectoryOutcomes.VerifiedResolve(d, seq),
            var k when k == SupervisorDecisionKinds.AskHuman => TrajectoryOutcomes.AnsweredAsk(d, seq),
            _ => TrajectoryOutcomes.Handled(d, seq),
        };
    }
}

/// <summary>The durable-shape outcome builders the environments fold — the SAME <c>SupervisorOutcome</c> shapes the executor writes, so the decider's rendered context is faithful to production.</summary>
internal static class TrajectoryOutcomes
{
    public static SupervisorPriorDecision Plan(SupervisorDecision d, long seq) =>
        Prior(d, seq, JsonSerializer.Serialize(new { planned = new[] { "s1", "s2" } }, AgentJson.Options));

    public static SupervisorPriorDecision AllSucceeded(SupervisorDecision d, long seq)
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var staged = JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options);
        var results = ids.Select((id, i) => new SupervisorAgentResult { AgentRunId = id, Status = "Succeeded", Summary = $"implemented subtask {i + 1}; unit tests green", ProducedBranch = $"agent/s{i + 1}" }).ToArray();
        return Prior(d, seq, SupervisorOutcome.FoldAgentResults(staged, results));
    }

    public static SupervisorPriorDecision OneFailed(SupervisorDecision d, long seq)
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var staged = JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options);
        var results = new[]
        {
            new SupervisorAgentResult { AgentRunId = ids[0], Status = "Succeeded", Summary = "implemented subtask 1; unit tests green", ProducedBranch = "agent/s1" },
            new SupervisorAgentResult { AgentRunId = ids[1], Status = "Failed", Error = "build failed: missing symbol referenced by subtask 2" },
        };
        return Prior(d, seq, SupervisorOutcome.FoldAgentResults(staged, results));
    }

    public static SupervisorPriorDecision VerifiedResolve(SupervisorDecision d, long seq)
    {
        var id = Guid.NewGuid();
        var staged = JsonSerializer.Serialize(new { agentRunIds = new[] { id }, agentCount = 1 }, AgentJson.Options);
        var resolver = new SupervisorAgentResult { AgentRunId = id, Status = "Succeeded", Summary = $"reconciled the conflict; build and the full test suite pass {SupervisorResolverRecipe.TestsPassedMarker}", ProducedBranch = "resolve/head" };
        return Prior(d, seq, SupervisorOutcome.FoldAgentResults(staged, new[] { resolver }));
    }

    public static SupervisorPriorDecision CleanMerge(SupervisorDecision d, long seq) =>
        Prior(d, seq, JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch = "codespace/integration/head" } }, AgentJson.Options));

    public static SupervisorPriorDecision ConflictedMerge(SupervisorDecision d, long seq) =>
        Prior(d, seq, JsonSerializer.Serialize(new { integration = new { status = "Conflicted", outcomes = new[] { new { conflictedFiles = new[] { "src/Signup.cs" }, fallbackBranch = "agent/s1" } } } }, AgentJson.Options));

    public static SupervisorPriorDecision IncompleteMerge(SupervisorDecision d, long seq) =>
        Prior(d, seq, JsonSerializer.Serialize(new { integration = new { status = "Incomplete", reason = "a subtask failed; retry it before integrating" } }, AgentJson.Options));

    /// <summary>An ask_human outcome with a REALISTIC affirmative answer folded exactly as production's rehydrate fold does (question + token + answer via <see cref="SupervisorOutcome.FoldAnswer"/>), so a cautious brain that asks once reads a real answer and converges instead of looping on an empty non-answer.</summary>
    public static SupervisorPriorDecision AnsweredAsk(SupervisorDecision d, long seq) =>
        Prior(d, seq, SupervisorOutcome.FoldAnswer($"Proceed with the plan to ship the goal? (turn {seq})", $"sim-ask-{seq}", "Yes — proceed and ship the work."));

    /// <summary>A generic "handled" outcome for an unrecognized verb so the loop simply continues (the scorer does not penalize a detour).</summary>
    public static SupervisorPriorDecision Handled(SupervisorDecision d, long seq) => Prior(d, seq, "{}");

    public static bool HasVerifiedResolve(IReadOnlyList<SupervisorPriorDecision> priors) =>
        priors.Any(p => p.DecisionKind == SupervisorDecisionKinds.Resolve && SupervisorOutcome.ReadResolutionVerdict(p.OutcomeJson) == SupervisorResolutionVerdict.Verified);

    public static bool HasRetry(IReadOnlyList<SupervisorPriorDecision> priors) =>
        priors.Any(p => p.DecisionKind == SupervisorDecisionKinds.Retry);

    private static SupervisorPriorDecision Prior(SupervisorDecision d, long seq, string outcomeJson) =>
        new() { Id = Guid.Empty, Sequence = seq, DecisionKind = d.Kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = d.PayloadJson, OutcomeJson = outcomeJson };
}

/// <summary>The outcome of a driven trajectory — the ordered decision kinds, whether it reached a terminal stop or ran into the turn cap, and the folded ledger the scorer reads for the real shippable result.</summary>
public sealed record SupervisorTrajectoryResult
{
    public required IReadOnlyList<string> Kinds { get; init; }
    public required bool ReachedStop { get; init; }
    public required bool HitTurnCap { get; init; }
    public required IReadOnlyList<SupervisorPriorDecision> Ledger { get; init; }
}

/// <summary>
/// The PURE trajectory scorer — the property a single decision can't measure: the run DRIVES TO COMPLETION (reaches a
/// stop within the cap/budget, never loops out) and STOPS AFTER PRODUCING A REAL SHIPPABLE RESULT (a clean integration
/// OR a verified resolution exists in the ledger at the stop — read by the SAME production reader the engine uses, so a
/// CONFLICTED merge or an UNVERIFIED resolve does NOT count as shipping), having PLANNED and DONE THE WORK, without
/// runaway re-planning or work-churn. Deterministic; no model, no I/O. Works across the happy / conflict / failure
/// environments uniformly — the recovery paths can only ship by resolving / retrying, which the ledger ship-check enforces.
/// </summary>
public static class SupervisorTrajectoryScore
{
    /// <summary>The replan ceiling — plan decisions after the first are rework; more than this is a non-converging brain.</summary>
    public const int MaxReplans = 2;

    /// <summary>The work-unit ceiling — agent-staging verbs (spawn/retry/resolve). The happy path needs ONE; a recovery path needs two; far more is a brain that churns on re-spawns without converging.</summary>
    public const int MaxWorkUnits = 4;

    public static (bool Ok, string Note) Score(SupervisorTrajectoryResult t)
    {
        var trail = string.Join("→", t.Kinds);

        if (!t.ReachedStop)
            return (false, t.HitTurnCap
                ? $"never stopped — hit the turn cap (the brain loops / doesn't drive to completion). Trajectory: {trail}"
                : $"did not reach a terminal stop within the time budget (deadline/cancellation). Trajectory: {trail}");

        // SHIP = a REAL reviewable head at the stop (a clean integration OR a verified resolution), read off the ledger by
        // the production reader — so a conflicted merge / unverified resolve / un-integrated fresh work does NOT count.
        if (SupervisorOutcome.ReadFinalIntegratedBranch(t.Ledger) is null)
            return (false, $"stopped WITHOUT shipping (no clean integration or verified resolution in the ledger) — quit early or left work unintegrated. Trajectory: {trail}");

        // Defense in depth on the ORDER: the brain must PLAN and DO THE WORK *before* it first attempts to ship — not
        // merge out of nothing, not plan after a ship. (The ledger ship-check above covers trailing un-integrated work;
        // this covers the ordering.) firstShip is guaranteed >= 0 because a non-null shippable head implies a merge/resolve.
        var firstShip = FirstIndex(t.Kinds, k => k == SupervisorDecisionKinds.Merge || k == SupervisorDecisionKinds.Resolve);

        var planIndex = FirstIndex(t.Kinds, k => k == SupervisorDecisionKinds.Plan);
        if (planIndex < 0 || planIndex > firstShip)
            return (false, $"shipped without PLANNING first — Trajectory: {trail}");

        var workIndex = FirstIndex(t.Kinds, SupervisorDecisionKinds.StagesAgents);
        if (workIndex < 0 || workIndex > firstShip)
            return (false, $"shipped without DOING THE WORK (no spawn/retry/resolve before the merge/resolve) — Trajectory: {trail}");

        var planCount = t.Kinds.Count(k => k == SupervisorDecisionKinds.Plan);
        if (planCount - 1 > MaxReplans)
            return (false, $"re-planned {planCount - 1} times (> {MaxReplans}) — not converging. Trajectory: {trail}");

        var workUnits = t.Kinds.Count(SupervisorDecisionKinds.StagesAgents);
        if (workUnits > MaxWorkUnits)
            return (false, $"staged work {workUnits} times (> {MaxWorkUnits}) — churning, not converging. Trajectory: {trail}");

        return (true, $"drove to completion: {trail}");
    }

    /// <summary>The index of the first kind matching the predicate, or -1.</summary>
    private static int FirstIndex(IReadOnlyList<string> kinds, Func<string, bool> match)
    {
        for (var i = 0; i < kinds.Count; i++)
            if (match(kinds[i])) return i;

        return -1;
    }
}
