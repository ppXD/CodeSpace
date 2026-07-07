namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The bounded durable supervisor lane's load-bearing CONSTANTS (PR-E). The lane is ALWAYS ON — it graduated from its
/// former <c>CODESPACE_SUPERVISOR_LANE_ENABLED</c> feature gate once the durable substrate + the loopability arc
/// (validator → dependency gate → per-unit acceptance → withhold) proved out; the agent.supervisor node, the reaper
/// job, and the stuck-run reconciler now run unconditionally. The fail-closed bounds below are counted from the durable
/// ledger so they survive restart/replay and can't be reset by re-entering the node (each pinned by a unit test, Rule 8).
/// </summary>
public static class SupervisorLane
{
    /// <summary>
    /// The CEILING for the best-effort no-progress guard (<c>SupervisorGoalPlan.NoProgressCeiling</c>): an
    /// operator's <c>MaxNoProgressDecisions</c> is clamped to <c>[1, DecisionBudget]</c>. A supervised run has
    /// no hard round cap — it loops until DONE, bounded only by the no-progress guard, the total-spawn cap, and
    /// the cost cap. This constant just keeps the no-progress cap from being set implausibly high.
    /// </summary>
    public const int DecisionBudget = 30;

    /// <summary>
    /// The default cap on how many agents one supervisor run may spawn IN TOTAL across the whole run (PR-E E5),
    /// summed from the durable ledger. Load-bearing + fail-closed: at the cap a further spawn FORCE-STOPS the run
    /// with a distinct terminal reason. An operator's <c>MaxTotalSpawns</c> tunes it within
    /// <c>[1, MaxTotalSpawnsCeiling]</c>. Pinned by a unit test (Rule 8).
    /// </summary>
    public const int DefaultMaxTotalSpawns = 50;

    /// <summary>The hard ceiling an operator's <c>MaxTotalSpawns</c> is clamped to — a fat-fingered config can't disable the bound. Pinned by a unit test (Rule 8).</summary>
    public const int MaxTotalSpawnsCeiling = 1_000;

    /// <summary>
    /// The default cap on consecutive decisions that produce NO new SETTLED agent result before the no-progress
    /// guard FORCE-STOPS the run (PR-E E5). BEST-EFFORT (demoted per the design): it stops a decider that loops
    /// without ever advancing the work; a long-running spawn whose agents haven't settled yet does NOT trip it
    /// (its decision is a park, not a fresh decided turn). Counted from the durable ledger. Pinned (Rule 8).
    /// </summary>
    public const int DefaultMaxNoProgressDecisions = 8;

    /// <summary>
    /// The cap on supervisor-spawns-supervisor nesting depth (PR-E E5) — reuses the <c>SubworkflowService.MaxDepth</c>
    /// precedent (a supervisor whose ancestor chain already has this many supervisor runs FORCE-STOPS at turn 0,
    /// before deciding). Guards against a recursive supervisor fan-out exhausting the deployment. Pinned (Rule 8).
    /// </summary>
    public const int MaxSupervisorDepth = 8;

    /// <summary>
    /// The default cap on how many <c>resolve</c> attempts one supervisor run may make against a conflicted
    /// integration (resolver loop #379) — small on purpose: a resolution that fails this many times should fall
    /// back fail-safe to the humans (the K agent branches remain), never an unbounded resolve loop burning agents +
    /// tokens. An operator's <c>MaxResolveAttempts</c> tunes it within <c>[1, MaxResolveAttemptsCeiling]</c>. Pinned (Rule 8).
    /// </summary>
    public const int DefaultMaxResolveAttempts = 1;

    /// <summary>The hard ceiling an operator's <c>MaxResolveAttempts</c> is clamped to — a conflict the model can't reconcile in a few tries is for a human, not an infinite retry. Pinned (Rule 8).</summary>
    public const int MaxResolveAttemptsCeiling = 5;

    /// <summary>Wall-clock cap (seconds) for the OBJECTIVE acceptance grade — re-cloning a resolver's branch and running the operator's acceptance command (L4 A3). A hung check is a non-accept, not a hang. An operator-tunable field is a follow-up. Pinned (Rule 8).</summary>
    public const int AcceptanceGradeTimeoutSeconds = 120;

    /// <summary>
    /// P1.3 — the heartbeat interval a long SEQUENTIAL multi-target/multi-gate grade emits a ledger record at, so
    /// the reconciler's staleness check (<see cref="StuckRunReconcilerService.LedgerLivenessWindow"/>, 5 min) never
    /// mistakes an actively-grading run for an abandoned one. Comfortably under the liveness window (30% of it) so
    /// a heartbeat always lands well before staleness would trip, even accounting for scheduling jitter. Pinned
    /// (Rule 8) — widening it risks the exact false-abandon this exists to prevent.
    /// </summary>
    public static readonly TimeSpan AcceptanceGradeHeartbeatInterval = TimeSpan.FromSeconds(90);
}
