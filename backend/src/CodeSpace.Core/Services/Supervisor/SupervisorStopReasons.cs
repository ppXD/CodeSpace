namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The DISTINCT terminal reasons a fail-closed bound or governance refusal stamps on a forced <c>stop</c>
/// decision (PR-E E5). Each is surfaced as the node's terminal <c>reason</c> output, so an operator sees
/// EXACTLY which bound stopped the run (never a silent truncation, never an unbounded run). The literals are
/// load-bearing (a re-entry after a bound tripped re-derives the same forced stop deterministically) + pinned
/// by a unit test (Rule 8) so a rename is a visible decision.
/// </summary>
public static class SupervisorStopReasons
{
    /// <summary>The total-spawned cap (<c>MaxTotalSpawns</c>) would be breached by a further spawn — the run has spawned its allotted agents.</summary>
    public const string TotalSpawnCapReached = "total spawn cap reached";

    /// <summary>The run's REALIZED spend (summed agent token cost) EXCEEDED <c>MaxCostUsd</c> (SOTA #4) — a further spend-incurring decision is refused. Realized-spend backpressure: exactly-at-budget proceeds, spend ABOVE budget force-STOPs (a terminal stop salvages already-paid-for work).</summary>
    public const string CostCapReached = "cost cap reached";

    /// <summary>A single spawn decision's fan-out (K) exceeds the per-decision cap (<c>MaxParallelism</c> ≤ the schema maxItems) — refused.</summary>
    public const string SpawnFanOutExceedsCap = "spawn fan-out exceeds cap";

    /// <summary>The supervisor is nested beyond <c>MaxSupervisorDepth</c> supervisor-ancestors — a recursive supervisor fan-out is refused at turn 0.</summary>
    public const string DepthCapExceeded = "supervisor nesting cap exceeded";

    /// <summary>BEST-EFFORT: too many consecutive decisions produced no new settled agent result — the run is making no progress.</summary>
    public const string NoProgress = "no progress";

    /// <summary>The S3 plan-confirmation gate has NO surface to ask on (the run has no usable conversation — e.g. a task launch, which does not wire one yet) — the run stops rather than spawning an unconfirmed plan (fail-closed, no silent bypass).</summary>
    public const string PlanConfirmationUnavailable = "plan confirmation unavailable (no conversation surface)";

    /// <summary>The DC-2b delivery gate found the required pull request UNSATISFIED (nothing published to open one from, policy-skipped, or failed) and the run has NO conversation surface to adjudicate on — it stops with the delivery diagnosis instead of grinding no-progress turns on unanswerable parks (H1; mirrors <see cref="PlanConfirmationUnavailable"/>).</summary>
    public const string DeliveryAdjudicationUnavailable = "delivery contract unsatisfied (no conversation surface to adjudicate)";

    /// <summary>A spawn/retry was refused because the latest plan version stands REJECTED (the operator answered its confirmation with revision feedback) and no revised version has been authored — a rejected plan may never be executed.</summary>
    public const string RejectedPlanSpawnRefused = "rejected plan spawn refused";

    /// <summary>The governance gate DENIED a side-effecting decision at the run's approval policy tier — refused (fail-closed, no side effect).</summary>
    public const string GovernanceDenied = "governance denied the side effect";

    /// <summary>The resolver loop (#379) exhausted its <c>MaxResolveAttempts</c> budget — a further <c>resolve</c> is refused so a conflict that won't reconcile falls back fail-safe to the humans (the K agent branches remain), never an unbounded resolve loop.</summary>
    public const string ResolveAttemptsExceeded = "resolve attempts exhausted";

    /// <summary>The model plane stayed unavailable past the ENTIRE infra-park window (P1.1): the brain call kept failing transient/rate-limited through every in-call retry AND every durable park-and-wake along the exponential ladder — the run stops honestly (a degraded <c>Stopped</c>, never a fake success) instead of parking forever.</summary>
    public const string ModelPlaneUnavailable = "model plane unavailable";

    /// <summary>A model-authored <c>plan</c> is structurally INVALID — its <c>DependsOn</c> graph has a dangling reference (a dependency on a subtask the plan never declares) or a cycle, so the dependency gate could never satisfy it. The Tier-0 validator force-STOPs at plan time rather than letting the run spin on deferred spawns until the no-progress bound — fail-fast + legible, never a silent stall.</summary>
    public const string PlanInvalid = "plan structurally invalid";
}
