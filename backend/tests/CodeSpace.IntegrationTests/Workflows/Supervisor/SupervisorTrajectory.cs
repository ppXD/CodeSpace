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

    /// <summary>Run <paramref name="decider"/> turn by turn over the happy-path environment until it stops or <paramref name="maxTurns"/> is reached. Returns the ordered decision kinds + whether a terminal stop was reached.</summary>
    public static async Task<SupervisorTrajectoryResult> RunAsync(ISupervisorDecider decider, int maxTurns, CancellationToken cancellationToken)
    {
        var priors = new List<SupervisorPriorDecision>();
        var kinds = new List<string>();

        for (var turn = 0; turn < maxTurns; turn++)
        {
            var context = new SupervisorTurnContext { Goal = "Ship the feature end to end", TurnNumber = turn, PriorDecisions = priors.ToList(), SupervisorModelId = Brain };

            var decision = await decider.DecideAsync(context, cancellationToken).ConfigureAwait(false);
            kinds.Add(decision.Kind);

            if (decision.IsTerminal) return new SupervisorTrajectoryResult { Kinds = kinds, ReachedStop = true, HitTurnCap = false };

            priors.Add(SimulateOutcome(decision, turn));
        }

        return new SupervisorTrajectoryResult { Kinds = kinds, ReachedStop = false, HitTurnCap = true };
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

            // ask_human / anything else: a generic "handled" outcome so the loop continues; the scorer flags an
            // unexpected detour on the clean happy path.
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
/// a stop within the cap, never loops to the cap) and STOPS AFTER SHIPPING (a merge or a verified resolve preceded
/// the stop — it didn't quit empty), without runaway re-planning. Deterministic; no model, no I/O.
/// </summary>
public static class SupervisorTrajectoryScore
{
    /// <summary>The replan ceiling — plan decisions after the first are rework; more than this on the SUCCESS path is a non-converging brain.</summary>
    public const int MaxReplans = 2;

    public static (bool Ok, string Note) Score(SupervisorTrajectoryResult t)
    {
        if (!t.ReachedStop)
            return (false, t.HitTurnCap ? "never stopped — hit the turn cap (the brain loops / doesn't drive to completion)" : "no terminal stop");

        var nonStop = t.Kinds.Where(k => k != SupervisorDecisionKinds.Stop).ToList();

        if (!nonStop.Contains(SupervisorDecisionKinds.Merge) && !nonStop.Contains(SupervisorDecisionKinds.Resolve))
            return (false, $"stopped WITHOUT shipping (no merge/resolve before stop) — quit early. Trajectory: {string.Join("→", t.Kinds)}");

        var planCount = t.Kinds.Count(k => k == SupervisorDecisionKinds.Plan);
        if (planCount - 1 > MaxReplans)
            return (false, $"re-planned {planCount - 1} times (> {MaxReplans}) — not converging. Trajectory: {string.Join("→", t.Kinds)}");

        return (true, $"drove to completion: {string.Join("→", t.Kinds)}");
    }
}
