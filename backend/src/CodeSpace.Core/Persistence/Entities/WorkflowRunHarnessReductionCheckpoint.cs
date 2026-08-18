namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The durable POSITION AND REDUCED STATE of one incremental reduction over a
/// <see cref="WorkflowRunHarnessExecution"/>'s captured records — the row a re-attach resumes from instead of folding
/// only the tail it can still see.
///
/// <para>One row per (execution, reducer kind). <see cref="ReducerKind"/> carries its own <c>/vN</c> and is immutable,
/// so a reduction whose state shape changes is a new kind stored BESIDE the old one rather than a rewrite that hands an
/// old reader a state it cannot parse. <see cref="ContractVersion"/> is immutable for the same reason.</para>
///
/// <para>The consumed count is stated THREE ways and the database refuses any write where they disagree: the frontier's
/// own per-stream sum, <see cref="RecordsConsumed"/>, and the reduced state's own <c>recordsConsumed</c> field. That is
/// what makes "a checkpoint may never claim a position the reducer has not actually consumed" refusable rather than
/// merely intended — bumping the frontier without the state, or the state without the frontier, is rejected.</para>
///
/// <para>The frontier's MONOTONICITY is the one anti-resurrection invariant the database itself holds: a displaced
/// reducer cannot rewind the row to its own older prefix. It does not authenticate the writer, and no row trigger can —
/// a trigger sees OLD and NEW, never which session sent them, so a displaced reducer whose frontier is NOT behind the
/// stored one is accepted here. Holdership is proved only by the writer carrying
/// <c>WHERE reducer_owner_id = @me AND reducer_fence = @observed AND revision = @observed</c> on its own writes, and
/// every writer of this row owes that predicate.</para>
///
/// <para>Bookkeeping, never authority: the owning Agent Run's status remains the only outcome authority, and nothing
/// here is read by completion, terminal decision, planner, oracle or model routing.</para>
/// </summary>
public sealed class WorkflowRunHarnessReductionCheckpoint : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }

    /// <summary>Denormalized Agent Run scope, proved by the composite foreign key to belong to <see cref="ExecutionId"/>.</summary>
    public Guid AgentRunId { get; set; }

    public Guid ExecutionId { get; set; }

    /// <summary>Immutable <c>&lt;kind&gt;/v&lt;major&gt;</c> of the reduction that produced <see cref="ReducedStateJson"/>.</summary>
    public string ReducerKind { get; set; } = string.Empty;

    /// <summary>Immutable data-contract version of the two JSON payloads; the state must carry the same value.</summary>
    public int ContractVersion { get; set; } = 1;

    /// <summary>The serialized reduction position — <c>{"streams":[{"streamId","nextOrdinal"}]}</c>, the contract
    /// record's own shape so a writer never has to remember to unwrap it. Zero-based, monotonic per stream, each
    /// stream at most once, and no stream may leave it.</summary>
    public string PositionJson { get; set; } = "{\"streams\":[]}";

    /// <summary>Exactly the frontier's total, and exactly the reduced state's own count. Ordinals are zero-based, so a stream at <c>k</c> accounts for exactly <c>k</c> records.</summary>
    public long RecordsConsumed { get; set; }

    /// <summary>The bounded reduction of the consumed prefix. Opaque here apart from the three fields the guard cross-checks — its count, its contract version, and its prefix digest.</summary>
    public string ReducedStateJson { get; set; } = "{}";

    /// <summary>Who currently holds this reduction. NULL ⇒ unheld.</summary>
    public Guid? ReducerOwnerId { get; set; }

    /// <summary>Advances by exactly one on every ACQUISITION, and an acquisition over a still-live lease is refused — so each fence value is acquired by at most one owner. A release leaves it where it is. It is the value a writer carries in its OWN <c>WHERE</c> clause; the trigger cannot check it against the identity of whoever issued the statement.</summary>
    public long ReducerFence { get; set; }

    /// <summary>When the current lease lapses. While it is live the reduction cannot be reclaimed by another owner.</summary>
    public DateTimeOffset? ReducerLeaseExpiresAt { get; set; }

    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public uint Xmin { get; set; }

    public WorkflowRunHarnessExecution Execution { get; set; } = default!;
}
