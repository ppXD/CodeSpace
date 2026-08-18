using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// ONE physical try of a <see cref="WorkflowRunToolCall"/>. Tool identity, effect class and the canonical arguments
/// live on the logical call; transport, the fabric's own request id, the returned payload, the outcome and the wall
/// clock live here, so a retry stays individually auditable instead of collapsing into a last-write-wins summary.
///
/// <para><see cref="AttemptOrdinal"/> is one-based and CONTIGUOUS: the database accepts only the ordinal its call's
/// head names next, and advances that head itself, so a gap cannot be written even by a racing writer. At most one
/// attempt is in flight per call, enforced by both the insert guard and a partial unique index — which together are
/// what make "did this retry, and what did each try do" answerable rather than reconstructed. Deleting a try on its
/// own is refused for the same reason: the call's head is DERIVED and no delete walks it back, so pruning happens at
/// the call and cascades from there.</para>
///
/// <para>Schema-only in this slice. <see cref="Status"/> is the OBSERVED outcome of this try:
/// <see cref="ToolCallAttemptStatus.Denied"/> belongs here because nothing executed, which is a fact about the
/// invocation — whereas the approval states remain in <c>tool_call_ledger</c>, which owns the governance state
/// machine and exactly-once. This plane records what happened; it decides nothing.</para>
/// </summary>
public sealed class WorkflowRunToolCallAttempt : IEntity<Guid>
{
    public Guid Id { get; set; }

    /// <summary>Denormalized tenant scope, proved against the parent call by a composite foreign key rather than trusted.</summary>
    public Guid TeamId { get; set; }

    /// <summary>Denormalized workflow-run scope for high-volume run queries, proved by the same composite foreign key.</summary>
    public Guid WorkflowRunId { get; set; }

    public Guid ToolCallId { get; set; }

    /// <summary>One-based physical attempt order within the logical call, contiguous from one.</summary>
    public int AttemptOrdinal { get; set; }

    /// <summary>
    /// The earlier FINISHED attempt of the same call this try retries. Not derivable as ordinal minus one: a third try
    /// may re-issue the first request rather than the second, which is exactly the case a reconstructed lineage gets
    /// wrong. A composite foreign key proves same-call membership; the guard proves the ordinal is lower and the
    /// retried attempt is no longer live.
    /// </summary>
    public Guid? RetryOfAttemptId { get; set; }

    /// <summary>Why the retry happened. Required whenever <see cref="RetryOfAttemptId"/> is set — a retry that cannot say why is the fact the audit needed most.</summary>
    public string? RetryReason { get; set; }

    /// <summary>Open transport protocol name/version, e.g. in-process/v1, mcp-stdio/v1 or mcp-uds/v1.</summary>
    public string? TransportKind { get; set; }

    /// <summary>Sanitized endpoint identity for the tool fabric; never a URL carrying credentials or secret query parameters.</summary>
    public string? EndpointFingerprint { get; set; }

    /// <summary>The fabric's OWN id for this try (a JSON-RPC or MCP request id). Unique within the call, so it is the idempotent admission key a capture adapter replays against.</summary>
    public string? InvocationId { get; set; }

    public ToolCallAttemptStatus Status { get; set; } = ToolCallAttemptStatus.Pending;

    /// <summary>Artifact holding the REDACTED tool result. Never inline, for the reason the arguments are not: large content in a hot row is how this plane falls over.</summary>
    public Guid? ResultArtifactId { get; set; }

    /// <summary>Canonical lowercase SHA-256 of the referenced result bytes. The digest the audit found missing — only an input hash was ever kept.</summary>
    public string? ResultDigest { get; set; }

    /// <summary>Artifact holding the REDACTED error body, when the fabric returned one.</summary>
    public Guid? ErrorArtifactId { get; set; }

    /// <summary>Canonical lowercase SHA-256 of the referenced ERROR bytes. Present exactly when that artifact is, for the reason the result carries one: an unverifiable reference is a reference no audit can trust, and the error body is not exempt from that.</summary>
    public string? ErrorDigest { get; set; }

    /// <summary>
    /// How the referenced result bytes relate to the wire, governing BOTH returned payloads: a tool's error body is as
    /// capable of quoting a credential back as its success body, so they share one redaction statement rather than
    /// leaving the error path the one nobody declared — while each payload keeps its OWN digest, so the error path is
    /// not the one nobody can verify either. NULL is the honest birth state and may be filled once; once stated, it
    /// and the payload columns around it are immutable.
    /// </summary>
    public NativeRecordRedaction? ResultRedaction { get; set; }

    /// <summary>The named pass that produced the referenced bytes. Required whenever either payload artifact is referenced. It proves a redactor RAN, never that it was correct.</summary>
    public string? RedactionPolicy { get; set; }

    /// <summary>How the observation was obtained, e.g. in-process, harness-native or controlled-proxy.</summary>
    public string CaptureSource { get; set; } = "unknown";

    /// <summary>The shared six-state capture vocabulary applied to the RESULT; evidence quality, never tool success.</summary>
    public WorkflowRunCaptureCompleteness CaptureCompleteness { get; set; } = WorkflowRunCaptureCompleteness.Unavailable;

    /// <summary>When this try was dispatched. Immutable, and always known because the row is written at dispatch.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When this try reached its outcome. Present exactly when the status is terminal, which is the per-attempt timing the audit found nowhere.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public long Revision { get; set; } = 1;

    public int SchemaVersion { get; set; } = WorkflowRunDataContract.CurrentVersion;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }

    /// <summary>Typed reason for every non-succeeded terminal, required so an unknown outcome can never be read as a clean one.</summary>
    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }
    public uint Xmin { get; set; }

    public WorkflowRunToolCall ToolCall { get; set; } = default!;
}

/// <summary>
/// Observed outcome of ONE physical tool try. Deliberately not the ledger's status vocabulary: the approval states
/// (awaiting, expired) are governance decisions the ledger owns, and duplicating them here would make this plane a
/// second governance mechanism. Everything below is something the invocation was observed to do.
/// </summary>
public enum ToolCallAttemptStatus
{
    /// <summary>Dispatch recorded, nothing observed yet.</summary>
    Pending,

    /// <summary>The try is live, or its liveness has not yet been contradicted.</summary>
    Running,

    /// <summary>The tool returned successfully. The only terminal that carries no error code.</summary>
    Succeeded,

    /// <summary>The tool ran and reported failure.</summary>
    Failed,

    /// <summary>The invocation was refused and nothing executed — an observed outcome, not a governance state.</summary>
    Denied,

    /// <summary>The try was cancelled before it produced an outcome.</summary>
    Cancelled,

    /// <summary>The try exceeded its allowance with no outcome from the tool.</summary>
    TimedOut,

    /// <summary>The try's real outcome is unknown and stays that way — it may or may not have landed its effect. Never collapsible into Failed, because a side effect that possibly committed is not a side effect that did not.</summary>
    Indeterminate,
}
