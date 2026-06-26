namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// An ACTIVE-rerun lease: a per-branch claim that prevents two CONCURRENT reruns from re-running the SAME
/// <c>(OriginalRunId, MapNodeId, BranchIndex)</c> at once — which, for a side-effecting branch body, would
/// double-fire the effect. One row per re-run branch in a fork; held while the fork is in flight, released the
/// moment it reaches a terminal state.
///
/// <para>The guarantee rides a UNIQUE PARTIAL index over <c>(original_run_id, map_node_id, branch_index)</c>
/// <c>WHERE status = 'in_progress'</c> (in the migration): a second rerun whose branch set OVERLAPS an in-flight
/// lease loses the INSERT on 23505 → a typed <see cref="Services.Workflows.RerunAlreadyInProgressException"/> →
/// 409, and its whole fork rolls back with the command transaction. DISJOINT branch sets never collide.</para>
///
/// <para>Complements — does NOT duplicate — the OperationId idempotency layer: that one dedups a SAME-token
/// resubmit (returns the prior fork, BEFORE any lease is taken); the lease blocks DISTINCT-token concurrent
/// overlap. The lease is acquired only on the path that mints a genuinely new fork.</para>
///
/// <para>Release is keyed on <see cref="ForkRunId"/>: the engine flips the lease the instant the fork completes
/// (inline, low-latency), and the reconciler's terminal-join sweep is the complete backstop (it releases every
/// lease whose fork reached Success/Failure/Cancelled — including a crash the abandoned-Running sweep first turns
/// into Failure, and a cancel that bypasses the inline path). Both run FKs cascade-delete, so a hard-deleted run
/// also frees its leases. A LEGITIMATELY suspended fork (parked on a branch approval gate) KEEPS its lease — the
/// rerun genuinely is still in progress, so a concurrent re-rerun of that branch stays blocked until it resolves.</para>
/// </summary>
public class WorkflowRerunLease
{
    public Guid Id { get; set; }

    /// <summary>The ORIGINAL run whose branch is being re-run (the fork's <see cref="WorkflowRun.ParentRunId"/>).</summary>
    public Guid OriginalRunId { get; set; }

    /// <summary>The top-level <c>flow.map</c> node whose branch is leased.</summary>
    public string MapNodeId { get; set; } = default!;

    /// <summary>The 0-based element index of the leased branch.</summary>
    public int BranchIndex { get; set; }

    /// <summary>The Rerun fork holding this lease — the release key (the lease frees when this run is terminal).</summary>
    public Guid ForkRunId { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>"in_progress" | "released" — only "in_progress" rows participate in the unique-partial conflict.</summary>
    public string Status { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when the lease leaves "in_progress" (inline on fork completion, or by the reconciler sweep).</summary>
    public DateTimeOffset? ReleasedAt { get; set; }
}
