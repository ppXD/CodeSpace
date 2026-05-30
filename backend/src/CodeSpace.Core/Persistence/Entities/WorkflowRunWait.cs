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

    /// <summary><c>Pending</c> until a resume signal arrives, then <c>Resolved</c>. See <see cref="WorkflowWaitStatuses"/>.</summary>
    public string Status { get; set; } = WorkflowWaitStatuses.Pending;

    /// <summary>The resume payload, set when resolved. Injected as the node's ResumePayload on re-run.</summary>
    public string? PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
