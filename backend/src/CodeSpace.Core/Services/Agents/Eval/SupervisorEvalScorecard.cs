using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// The pure scorer turning a team's supervisor runs into a <see cref="SupervisorScorecard"/> — the measurement
/// spine for the L4-core supervisor lane (PR-E E6), the sibling of <see cref="EvalScorecard"/> for agent runs. It
/// answers "is the SUPERVISOR actually working": how many decisions it took, how much it re-planned, how many
/// agents it spawned and how many of THOSE truly succeeded, how often it asked a human, how long it ran, and which
/// outcome it landed in. Deterministic + DB-free, so it unit-tests exhaustively over a crafted ledger.
///
/// <para>HONEST by construction: an in-flight run (not yet terminal) is reported as <see cref="SupervisorOutcomes.NotScored"/>
/// and EXCLUDED from the roll-up averages (mirrors how <see cref="EvalScorecard"/> excludes in-flight agent runs);
/// the spawn success rate is computed from the REAL <see cref="AgentRunStatus"/> of the spawned agents (never the
/// decider's self-report); time-to-stop comes from real timestamps. No placeholder numbers.</para>
/// </summary>
public static class SupervisorEvalScorecard
{
    public static SupervisorScorecard Compute(IReadOnlyList<SupervisorRunOutcome> runs) =>
        Build(runs.Select(run => (run, score: Score(run))).ToList());

    /// <summary>Score ONE run — pure over its decision ledger + spawned-agent terminals. An in-flight run is marked not-scored (outcome derived later from the terminal stop only).</summary>
    public static SupervisorRunScore Score(SupervisorRunOutcome run)
    {
        var decisions = run.Decisions;

        var planCount = Count(decisions, SupervisorDecisionKinds.Plan);
        var spawnedAgents = decisions.Sum(d => d.StagedAgentCount);
        var spawnedSucceeded = run.SpawnedAgentStatuses.Count(s => s == AgentRunStatus.Succeeded);

        var isTerminal = run.TerminalStatus is not null;

        return new SupervisorRunScore
        {
            SupervisorRunId = run.SupervisorRunId,
            TotalDecisions = decisions.Count,
            PlanCount = planCount,
            SpawnCount = Count(decisions, SupervisorDecisionKinds.Spawn),
            RetryCount = Count(decisions, SupervisorDecisionKinds.Retry),
            ResolveCount = Count(decisions, SupervisorDecisionKinds.Resolve),
            MergeCount = Count(decisions, SupervisorDecisionKinds.Merge),
            AskHumanCount = Count(decisions, SupervisorDecisionKinds.AskHuman),
            StopCount = Count(decisions, SupervisorDecisionKinds.Stop),
            ReplanRounds = Math.Max(0, planCount - 1),
            SpawnedAgents = spawnedAgents,
            SpawnSuccessRate = spawnedAgents == 0 ? 0 : (double)spawnedSucceeded / spawnedAgents,
            TimeToStopSeconds = isTerminal ? run.TimeToStopSeconds : null,
            Outcome = isTerminal ? ClassifyOutcome(decisions, run.TerminalStatus!.Value) : SupervisorOutcomes.NotScored,
            NotScored = !isTerminal,
        };
    }

    /// <summary>
    /// Map the terminal stop's reason/label into a canonical outcome bucket. A FORCED stop stamps a
    /// <see cref="SupervisorStopReasons"/> value (the governance reason → governance-denied; every other bound →
    /// budget-exhausted); a decider stop stamps its <c>Outcome</c> label (a success-ish word → completed, else
    /// aborted). When the run reached a terminal state with NO supervisor stop decision (the supervisor node
    /// failed mid-run, or an operator cancelled it), the run's REAL <see cref="WorkflowRunStatus"/> decides the
    /// bucket HONESTLY — a Failure → aborted, a Cancelled → cancelled, only a Success → completed (the neutral
    /// "reached its end"). A non-success terminal run is never folded into completed. Reads the LAST stop's
    /// reason (the terminal one).
    /// </summary>
    private static string ClassifyOutcome(IReadOnlyList<SupervisorDecisionSummary> decisions, WorkflowRunStatus terminalStatus)
    {
        var reason = decisions.LastOrDefault(d => d.Kind == SupervisorDecisionKinds.Stop)?.StopReason;

        if (string.IsNullOrWhiteSpace(reason)) return ClassifyByRunStatus(terminalStatus);

        if (reason == SupervisorStopReasons.GovernanceDenied) return SupervisorOutcomes.GovernanceDenied;

        if (IsBoundReason(reason)) return SupervisorOutcomes.BudgetExhausted;

        return IsSuccessLabel(reason) ? SupervisorOutcomes.Completed : SupervisorOutcomes.Aborted;
    }

    /// <summary>A terminal run with no supervisor stop decision: bucket by the run's own honest terminal status — Cancelled → cancelled, Failure → aborted, Success → completed.</summary>
    private static string ClassifyByRunStatus(WorkflowRunStatus terminalStatus) => terminalStatus switch
    {
        WorkflowRunStatus.Cancelled => SupervisorOutcomes.Cancelled,
        WorkflowRunStatus.Failure => SupervisorOutcomes.Aborted,
        _ => SupervisorOutcomes.Completed,
    };

    /// <summary>True when the reason is one of the fail-closed bound stops (round/decision budget, total-spawn cap, per-decision fan-out cap, depth cap, no-progress) — all roll up to budget-exhausted.</summary>
    private static bool IsBoundReason(string reason) =>
        reason is SupervisorStopReasons.BudgetExhausted
            or SupervisorStopReasons.TotalSpawnCapReached
            or SupervisorStopReasons.SpawnFanOutExceedsCap
            or SupervisorStopReasons.DepthCapExceeded
            or SupervisorStopReasons.NoProgress;

    /// <summary>A decider stop label that means "done well" (vs failed/abandoned). Case-insensitive; "completed"/"success"/"done"/"ok" count as success.</summary>
    private static bool IsSuccessLabel(string label) =>
        label.Trim().ToLowerInvariant() is "completed" or "complete" or "success" or "succeeded" or "done" or "ok";

    private static int Count(IReadOnlyList<SupervisorDecisionSummary> decisions, string kind) => decisions.Count(d => d.Kind == kind);

    /// <summary>
    /// Fold the per-run scores into the cross-run roll-up: averages + overall ground-truth spawn success + the
    /// outcome distribution, over the SCORED (terminal) runs only. The overall spawn success is summed from the
    /// raw <see cref="SupervisorRunOutcome.SpawnedAgentStatuses"/> (the REAL agent terminals) so it stays exact —
    /// never reconstructed from a per-run rate. <c>Runs</c> preserves the input order verbatim (the service feeds
    /// runs most-recent first, so the per-run list comes back most-recent first).
    /// </summary>
    private static SupervisorScorecard Build(IReadOnlyList<(SupervisorRunOutcome Run, SupervisorRunScore Score)> all)
    {
        var scored = all.Where(x => !x.Score.NotScored).ToList();

        var totalSpawned = scored.Sum(x => x.Score.SpawnedAgents);
        var totalSucceeded = scored.Sum(x => x.Run.SpawnedAgentStatuses.Count(s => s == AgentRunStatus.Succeeded));

        var times = scored
            .Where(x => x.Score.TimeToStopSeconds is >= 0)
            .Select(x => x.Score.TimeToStopSeconds!.Value)
            .OrderBy(t => t)
            .ToList();

        var rollup = new SupervisorRollup
        {
            ScoredRuns = scored.Count,
            NotScoredRuns = all.Count - scored.Count,
            AvgDecisionsPerRun = scored.Count == 0 ? 0 : scored.Average(x => x.Score.TotalDecisions),
            AvgReplanRounds = scored.Count == 0 ? 0 : scored.Average(x => x.Score.ReplanRounds),
            OverallSpawnSuccessRate = totalSpawned == 0 ? 0 : (double)totalSucceeded / totalSpawned,
            P50TimeToStopSeconds = Percentile(times, 50),
            P95TimeToStopSeconds = Percentile(times, 95),
            OutcomeDistribution = scored
                .GroupBy(x => x.Score.Outcome, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        };

        return new SupervisorScorecard { Rollup = rollup, Runs = all.Select(x => x.Score).ToList() };
    }

    /// <summary>Nearest-rank percentile over a pre-sorted ascending list; null when empty. Mirrors <see cref="EvalScorecard"/>'s percentile — deterministic.</summary>
    private static double? Percentile(IReadOnlyList<double> sorted, int p)
    {
        if (sorted.Count == 0) return null;

        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
