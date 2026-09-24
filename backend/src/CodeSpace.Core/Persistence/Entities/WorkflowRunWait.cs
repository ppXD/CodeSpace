using CodeSpace.Messages.Constants;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One node suspension. Written when a node returns <c>Suspended</c>; the run goes to
/// <c>WorkflowRunStatus.Suspended</c> and the engine returns. A resume signal (timer wake,
/// human approval, external callback) resolves the matching row — sets <see cref="Status"/> to
/// <c>Resolved</c> + the <see cref="PayloadJson"/> — flips the run back to Pending, and
/// re-dispatches. The durable walker rehydrates and injects <see cref="PayloadJson"/> as the
/// node's <c>ResumePayload</c> on re-run.
///
/// At most one outstanding (Pending) wait per (run, node, iteration) — a node parks on one
/// signal at a time. The immutable audit copy of the suspension is the <c>node.suspended</c>
/// record in <c>workflow_run_record</c>; this row is the mutable state the resume path acts on.
/// </summary>
public class WorkflowRunWait
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    public string NodeId { get; set; } = default!;
    public string IterationKey { get; set; } = string.Empty;

    /// <summary>Why the run is parked + how it wakes. One of <see cref="WorkflowWaitKinds"/>.</summary>
    public string WaitKind { get; set; } = default!;

    /// <summary>Opaque correlation id an approval / callback signal presents to resolve this wait.</summary>
    public string Token { get; set; } = default!;

    /// <summary>For <c>Timer</c> waits — the instant the scheduled resume fires. Null for approval/callback.</summary>
    public DateTimeOffset? WakeAt { get; set; }

    /// <summary><c>Pending</c> until a resume signal answers it (<c>Resolved</c>) or the run's terminal teardown closes it unanswered (<c>Discarded</c>). See <see cref="WorkflowWaitStatuses"/>.</summary>
    public string Status { get; set; } = WorkflowWaitStatuses.Pending;

    /// <summary>The node's suspend payload while parked, overwritten with the resume payload when resolved. Only a <c>Resolved</c> row's payload is injected as the node's ResumePayload on re-run — a <c>Discarded</c> row still holds the request.</summary>
    public string? PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Database-clock recovery selection time; retry fairness survives worker replacement. Not a completion receipt.</summary>
    public DateTimeOffset? LastAgentRecoveryAttemptAt { get; set; }
}
