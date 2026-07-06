using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// One supervisor decision reduced to the fields the scorer aggregates (a data noun, Rule 18.1) — the per-row
/// input the pure <c>SupervisorScorecard</c> folds. Decoupled from the <c>SupervisorDecisionRecord</c> entity so
/// the scorer is testable without a DB. The decisions are passed in <c>Sequence</c> order (the replay tape's
/// natural ordering), so "the first plan" vs "a replan" is positional.
/// </summary>
public sealed record SupervisorDecisionSummary
{
    /// <summary>The decision verb — a <see cref="SupervisorDecisionKinds"/> value (plan/spawn/retry/merge/ask_human/stop).</summary>
    public required string Kind { get; init; }

    /// <summary>Agents this decision staged (a spawn/retry's recorded <c>agentCount</c>; 0 for a synchronous verb). The honest count from the ledger outcome, not the decider's self-report.</summary>
    public required int StagedAgentCount { get; init; }

    /// <summary>The terminal stop reason / outcome label read off a <c>stop</c> decision's payload (a forced-stop <c>reason</c> or a decider <c>outcome</c>); null for a non-stop decision.</summary>
    public string? StopReason { get; init; }

    /// <summary>The OBJECTIVE acceptance verdict the server graded for a model-authored <c>stop</c>'s definition-of-done (folded onto the stop's outcome by L4 P1); <c>false</c> when the model's own acceptance check FAILED, <c>true</c> when it passed, <c>null</c> when the stop authored no acceptance check (or the row is not a stop). The scorer lets this OVERRIDE the self-reported <see cref="StopReason"/> label so the eval never scores the brain's word over the server's grade.</summary>
    public bool? AcceptancePassed { get; init; }
}

/// <summary>
/// One supervisor run reduced to everything the scorer needs (a data noun, Rule 18.1): the run's decisions in
/// Sequence order, the REAL terminal status of each agent the run spawned (so spawn success is computed from
/// ground truth, not the decider's claim), the run's first/last decision timestamps (real wall-clock), and
/// the run's own REAL terminal <see cref="WorkflowRunStatus"/> (null while in flight). Decoupled from the
/// entities so the scorer is pure + DB-free + exhaustively unit-testable.
/// </summary>
public sealed record SupervisorRunOutcome
{
    /// <summary>The supervisor run id (the WorkflowRun id) — surfaced on the per-run score so an operator can open the run.</summary>
    public required Guid SupervisorRunId { get; init; }

    /// <summary>The run's decisions in <c>Sequence</c> order.</summary>
    public required IReadOnlyList<SupervisorDecisionSummary> Decisions { get; init; }

    /// <summary>The REAL terminal status of every agent this run spawned (by spawn/retry), looked up from the AgentRun rows. An agent still in-flight (Queued/Running) contributes a non-terminal status, so it lowers the spawn success rate honestly rather than being counted as a success.</summary>
    public required IReadOnlyList<AgentRunStatus> SpawnedAgentStatuses { get; init; }

    /// <summary>Wall-clock seconds from the run's first decision to its terminal stop; null when the run has not yet stopped (so an in-flight run reports no time-to-stop).</summary>
    public double? TimeToStopSeconds { get; init; }

    /// <summary>The run's REAL terminal <see cref="WorkflowRunStatus"/> (Success / Failure / Cancelled) once the WorkflowRun reached a terminal state; null while the run is in flight. Carried (not a lossy bool) so the scorer can classify a terminal run that recorded NO supervisor stop decision by its honest run status — a Failure/Cancelled run never masquerades as completed. A non-null value means the run is scored.</summary>
    public WorkflowRunStatus? TerminalStatus { get; init; }
}

/// <summary>The canonical outcome buckets a scored supervisor run lands in — derived from the terminal stop's reason/label. <see cref="NotScored"/> is the honest marker for a still-in-flight run (excluded from the roll-up averages, like an in-flight agent run).</summary>
public static class SupervisorOutcomes
{
    /// <summary>The supervisor decided it was done — a decider <c>stop</c> with a success-ish label (the default for a normal completion).</summary>
    public const string Completed = "completed";

    /// <summary>A fail-closed bound force-stopped the run (total-spawn cap, depth cap, no-progress).</summary>
    public const string BudgetExhausted = "budget-exhausted";

    /// <summary>The governance gate denied a side-effecting decision → fail-closed force-stop.</summary>
    public const string GovernanceDenied = "governance-denied";

    /// <summary>The decider stopped with a failure/abandon label, OR the run reached a terminal <c>Failure</c> with no supervisor stop decision (e.g. the supervisor node failed: lane disabled mid-run, unreadable scope) — not a clean completion, not a bound.</summary>
    public const string Aborted = "aborted";

    /// <summary>The model authored a <c>stop</c> it labelled done, but its OWN model-authored acceptance check (the definition of done) FAILED the server's objective grade — the reviewable branch was withheld. Distinct from <see cref="Aborted"/> (the model didn't even claim success) and never folded into <see cref="Completed"/>: the eval reports the brain over-claiming completion as its own honest signal.</summary>
    public const string AcceptanceFailed = "acceptance-failed";

    /// <summary>An operator cancelled the run mid-flight (terminal <c>Cancelled</c>) with no supervisor stop decision — honest, never folded into completed.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The run has not reached a terminal stop yet — reported but excluded from the roll-up averages (honest: an in-flight run has no final score).</summary>
    public const string NotScored = "not-scored";
}

/// <summary>
/// The measurable shape of ONE supervisor run — what it decided, how much rework it did, how many agents it
/// spawned + how many of THOSE actually succeeded (the honest, ground-truth rate), how often it asked a human,
/// how long it took, and the outcome bucket it landed in. The supervisor-lane analogue of <see cref="HarnessScore"/>:
/// it turns "is the supervisor working" from an assertion into numbers an operator can audit. Team-scoped at the
/// service layer; only the caller team's runs ever appear.
/// </summary>
public sealed record SupervisorRunScore
{
    public required Guid SupervisorRunId { get; init; }

    /// <summary>Every decision the run recorded (the full ledger length).</summary>
    public required int TotalDecisions { get; init; }

    public required int PlanCount { get; init; }
    public required int SpawnCount { get; init; }
    public required int RetryCount { get; init; }
    public required int ResolveCount { get; init; }
    public required int MergeCount { get; init; }
    public required int AskHumanCount { get; init; }
    public required int StopCount { get; init; }

    /// <summary>Plan decisions AFTER the first — the rework / re-planning cycles (0 for a run that planned once or never).</summary>
    public required int ReplanRounds { get; init; }

    /// <summary>Total agents the run spawned (summed staged <c>agentCount</c> across every spawn/retry) — the honest count from the ledger outcomes.</summary>
    public required int SpawnedAgents { get; init; }

    /// <summary>Spawned agents that reached <see cref="AgentRunStatus.Succeeded"/> / total spawned, in 0..1; 0 when none were spawned. Computed from the REAL AgentRun terminal status — never the decider's self-report.</summary>
    public required double SpawnSuccessRate { get; init; }

    /// <summary>Wall-clock seconds from the first decision to the terminal stop; null when the run isn't terminal yet.</summary>
    public double? TimeToStopSeconds { get; init; }

    /// <summary>The outcome bucket (a <see cref="SupervisorOutcomes"/> value): completed / budget-exhausted / governance-denied / aborted / acceptance-failed / cancelled, or not-scored for an in-flight run.</summary>
    public required string Outcome { get; init; }

    /// <summary>True when the run has not reached a terminal stop — reported but excluded from the roll-up averages (honest).</summary>
    public required bool NotScored { get; init; }
}

/// <summary>
/// The cross-run roll-up over a team's scored supervisor runs — averages + the overall ground-truth spawn
/// success + the distribution of outcomes. In-flight (not-scored) runs are EXCLUDED from the averages (they have
/// no final score) but counted in <see cref="NotScoredRuns"/> so the operator sees how many are still running.
/// </summary>
public sealed record SupervisorRollup
{
    /// <summary>Scored (terminal) supervisor runs in the roll-up.</summary>
    public required int ScoredRuns { get; init; }

    /// <summary>Supervisor runs still in flight — reported but excluded from the averages.</summary>
    public required int NotScoredRuns { get; init; }

    /// <summary>Mean decisions per scored run; 0 when there are no scored runs.</summary>
    public required double AvgDecisionsPerRun { get; init; }

    /// <summary>Mean replan rounds per scored run; 0 when there are no scored runs.</summary>
    public required double AvgReplanRounds { get; init; }

    /// <summary>Agents that succeeded / total agents spawned across all scored runs, in 0..1; 0 when none were spawned. The honest cross-run spawn success from real agent terminals.</summary>
    public required double OverallSpawnSuccessRate { get; init; }

    /// <summary>Median / 95th-percentile time-to-stop over the scored runs that have one; null when none do.</summary>
    public double? P50TimeToStopSeconds { get; init; }
    public double? P95TimeToStopSeconds { get; init; }

    /// <summary>How many scored runs landed in each outcome bucket (a <see cref="SupervisorOutcomes"/> value → count), the failure-mode distribution.</summary>
    public required IReadOnlyDictionary<string, int> OutcomeDistribution { get; init; }
}

/// <summary>The team's supervisor-run scorecard — the cross-run roll-up plus the recent per-run scores. The supervisor-lane analogue of <see cref="AgentRunScorecard"/>.</summary>
public sealed record SupervisorScorecard
{
    public required SupervisorRollup Rollup { get; init; }

    /// <summary>The recent per-run scores (most-recent first), capped by the service so the payload stays bounded.</summary>
    public required IReadOnlyList<SupervisorRunScore> Runs { get; init; }
}
