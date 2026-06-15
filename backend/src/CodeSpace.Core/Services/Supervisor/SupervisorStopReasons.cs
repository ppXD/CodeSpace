namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The DISTINCT terminal reasons a fail-closed bound or governance refusal stamps on a forced <c>stop</c>
/// decision (PR-E E5). Each is surfaced as the node's terminal <c>reason</c> output, so an operator sees
/// EXACTLY which bound stopped the run (never a silent truncation, never an unbounded run). The literals are
/// load-bearing (a re-entry after a bound tripped re-derives the same forced stop deterministically) + pinned
/// by a unit test (Rule 8) so a rename is a visible decision.
///
/// <para><see cref="BudgetExhausted"/> stays on <c>SupervisorTurnService</c> as the historical E2 reason; the
/// E5 reasons live here together so the full bound vocabulary is legible in one place.</para>
/// </summary>
public static class SupervisorStopReasons
{
    /// <summary>The round/decision budget (<c>MaxRounds</c>, ≤ <c>SupervisorLane.DecisionBudget</c>) was reached — the run can take no more decisions.</summary>
    public const string BudgetExhausted = "budget exhausted";

    /// <summary>The total-spawned cap (<c>MaxTotalSpawns</c>) would be breached by a further spawn — the run has spawned its allotted agents.</summary>
    public const string TotalSpawnCapReached = "total spawn cap reached";

    /// <summary>A single spawn decision's fan-out (K) exceeds the per-decision cap (<c>MaxParallelism</c> ≤ the schema maxItems) — refused.</summary>
    public const string SpawnFanOutExceedsCap = "spawn fan-out exceeds cap";

    /// <summary>The supervisor is nested beyond <c>MaxSupervisorDepth</c> supervisor-ancestors — a recursive supervisor fan-out is refused at turn 0.</summary>
    public const string DepthCapExceeded = "supervisor nesting cap exceeded";

    /// <summary>BEST-EFFORT: too many consecutive decisions produced no new settled agent result — the run is making no progress.</summary>
    public const string NoProgress = "no progress";

    /// <summary>The governance gate DENIED a side-effecting decision at the run's approval policy tier — refused (fail-closed, no side effect).</summary>
    public const string GovernanceDenied = "governance denied the side effect";
}
