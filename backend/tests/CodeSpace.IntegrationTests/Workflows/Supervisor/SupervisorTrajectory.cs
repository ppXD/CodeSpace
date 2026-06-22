using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The MULTI-TURN trajectory harness (A3) — the honest follow-up to the single-decision golden eval. It drives a
/// supervisor decider TURN BY TURN over a SIMULATED happy-path environment: each decision's outcome is folded into
/// the ledger and fed back as the next turn's context, exactly as the real engine would, until the decider STOPS or
/// a turn cap is hit. This measures what a single decision cannot — does the brain DRIVE TO COMPLETION and STOP AT
/// THE RIGHT TIME (after shipping), rather than loop forever or quit empty.
///
/// <para>The environment is the SUCCESS path: spawned/retried work Succeeds, a merge comes back Clean. So the
/// canonical trajectory is plan → spawn → merge → stop. The decider under test is swapped: a scripted one pins the
/// harness + scorer (always-on), the REAL model proves its trajectory judgment against a live endpoint (the
/// real-model gate). Pure of Postgres — it folds contexts in memory via the same <c>SupervisorOutcome</c> helpers
/// the engine uses, so the context is what the brain really reads.</para>
/// </summary>
public static class SupervisorTrajectory
{
    private static readonly Guid Brain = SupervisorDecisionGoldenScenarios.BrainModelRowId;

    /// <summary>Run <paramref name="decider"/> turn by turn over the happy-path environment until it stops, <paramref name="maxTurns"/> is reached, or <paramref name="cancellationToken"/> (a wall-clock deadline) cancels it. A cancellation is converted into a clean non-stop result (scored as a failure) rather than thrown, so a slow real-model endpoint surfaces a legible verdict instead of an opaque CI timeout. Returns the ordered decision kinds + whether a terminal stop was reached.</summary>
    public static async Task<SupervisorTrajectoryResult> RunAsync(ISupervisorDecider decider, int maxTurns, CancellationToken cancellationToken)
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

            if (decision.IsTerminal) return new SupervisorTrajectoryResult { Kinds = kinds, ReachedStop = true, HitTurnCap = false };

            priors.Add(SimulateOutcome(decision, turn));
        }

        // Exhausted the turn cap (the brain loops) OR a deadline cancelled it — HitTurnCap distinguishes the two so the
        // scorer names the failure precisely (a true loop vs. a slow run that never converged inside the time budget).
        return new SupervisorTrajectoryResult { Kinds = kinds, ReachedStop = false, HitTurnCap = !cancellationToken.IsCancellationRequested };
    }

    /// <summary>The happy-path environment: fold the decided action into the durable-shape outcome the NEXT turn reads. Spawned/retried work SUCCEEDS; a merge is CLEAN; a resolve is VERIFIED — so a competent brain converges on a stop.</summary>
    private static SupervisorPriorDecision SimulateOutcome(SupervisorDecision decision, long sequence)
    {
        switch (decision.Kind)
        {
            case var k when k == SupervisorDecisionKinds.Plan:
                return Prior(k, sequence, decision.PayloadJson, JsonSerializer.Serialize(new { planned = new[] { "s1", "s2" } }, AgentJson.Options));

            case var k when k == SupervisorDecisionKinds.Spawn || k == SupervisorDecisionKinds.Retry:
                return Prior(k, sequence, decision.PayloadJson, SuccessfulAgentsOutcome(sequence));

            case var k when k == SupervisorDecisionKinds.Merge:
                return Prior(k, sequence, decision.PayloadJson, JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch = "codespace/integration/head" } }, AgentJson.Options));

            case var k when k == SupervisorDecisionKinds.Resolve:
                return Prior(k, sequence, decision.PayloadJson, VerifiedResolveOutcome(sequence));

            // ask_human: fold a REALISTIC affirmative answer — exactly as the production rehydrate fold supplies the
            // human's reply (SupervisorOutcome.FoldAnswer) — so the next turn reads "you asked X, the human answered Y"
            // and a legitimately-cautious model can converge to merge→stop, rather than a bare "{}" no-answer that would
            // wedge it into asking again. This keeps the simulated context faithful to what the brain really reads.
            case var k when k == SupervisorDecisionKinds.AskHuman:
                return Prior(k, sequence, decision.PayloadJson, AnsweredAskHumanOutcome(sequence));

            // Anything else (an unrecognized verb): a generic "handled" outcome so the loop simply continues. The scorer
            // does not penalize a detour — it scores convergence (a stop within the cap), shipping (a merge/resolve,
            // preceded by a plan AND the work), and the replan/work-churn ceilings.
            default:
                return Prior(decision.Kind, sequence, decision.PayloadJson, "{}");
        }
    }

    private static string SuccessfulAgentsOutcome(long sequence)
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var staged = JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options);
        var results = ids.Select((id, i) => new SupervisorAgentResult { AgentRunId = id, Status = "Succeeded", Summary = $"implemented subtask {i + 1}; unit tests green", ProducedBranch = $"agent/s{i + 1}" }).ToArray();
        return SupervisorOutcome.FoldAgentResults(staged, results);
    }

    private static string VerifiedResolveOutcome(long sequence)
    {
        var id = Guid.NewGuid();
        var staged = JsonSerializer.Serialize(new { agentRunIds = new[] { id }, agentCount = 1 }, AgentJson.Options);
        var resolver = new SupervisorAgentResult { AgentRunId = id, Status = "Succeeded", Summary = $"reconciled the conflict; build and the full test suite pass {SupervisorResolverRecipe.TestsPassedMarker}", ProducedBranch = "resolve/head" };
        return SupervisorOutcome.FoldAgentResults(staged, new[] { resolver });
    }

    /// <summary>The happy-path "human answered" outcome — an affirmative reply folded exactly as production's rehydrate fold does (question + correlation token + answer via <see cref="SupervisorOutcome.FoldAnswer"/>), so a cautious brain that asks once reads a real answer and converges instead of looping on an empty non-answer.</summary>
    private static string AnsweredAskHumanOutcome(long sequence) =>
        SupervisorOutcome.FoldAnswer($"Proceed with the plan to ship the goal? (turn {sequence})", $"sim-ask-{sequence}", "Yes — proceed and ship the work.");

    private static SupervisorPriorDecision Prior(string kind, long sequence, string payloadJson, string outcomeJson) =>
        new() { Id = Guid.Empty, Sequence = sequence, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson };
}

/// <summary>The outcome of a driven trajectory — the ordered decision kinds + whether it reached a terminal stop or ran into the turn cap.</summary>
public sealed record SupervisorTrajectoryResult
{
    public required IReadOnlyList<string> Kinds { get; init; }
    public required bool ReachedStop { get; init; }
    public required bool HitTurnCap { get; init; }
}

/// <summary>
/// The PURE trajectory scorer — the property a single decision can't measure: the run DRIVES TO COMPLETION (reaches
/// a stop within the cap/budget, never loops out) and STOPS AFTER DOING THE WORK AND SHIPPING IT (a plan, then the
/// work, then a merge or a verified resolve, preceded the stop — it neither quit empty nor merged out of nothing),
/// without runaway re-planning or work-churn. Deterministic; no model, no I/O.
/// </summary>
public static class SupervisorTrajectoryScore
{
    /// <summary>The replan ceiling — plan decisions after the first are rework; more than this on the SUCCESS path is a non-converging brain.</summary>
    public const int MaxReplans = 2;

    /// <summary>The work-unit ceiling — agent-staging verbs (spawn/retry/resolve). The happy path needs ONE; far more is a brain that ships then churns on re-spawns without converging (the turn cap alone would let it waste turns).</summary>
    public const int MaxWorkUnits = 4;

    public static (bool Ok, string Note) Score(SupervisorTrajectoryResult t)
    {
        var trail = string.Join("→", t.Kinds);

        if (!t.ReachedStop)
            return (false, t.HitTurnCap
                ? $"never stopped — hit the turn cap (the brain loops / doesn't drive to completion). Trajectory: {trail}"
                : $"did not reach a terminal stop within the time budget (deadline/cancellation). Trajectory: {trail}");

        var firstShip = FirstIndex(t.Kinds, k => k == SupervisorDecisionKinds.Merge || k == SupervisorDecisionKinds.Resolve);

        if (firstShip < 0)
            return (false, $"stopped WITHOUT shipping (no merge/resolve before stop) — quit early. Trajectory: {trail}");

        var planIndex = FirstIndex(t.Kinds, k => k == SupervisorDecisionKinds.Plan);
        if (planIndex < 0 || planIndex > firstShip)
            return (false, $"shipped without PLANNING first (no plan before the merge/resolve) — merged out of nothing. Trajectory: {trail}");

        var workIndex = FirstIndex(t.Kinds, SupervisorDecisionKinds.StagesAgents);
        if (workIndex < 0 || workIndex > firstShip)
            return (false, $"shipped without DOING THE WORK (no spawn/retry/resolve before the merge/resolve) — merged out of nothing. Trajectory: {trail}");

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
