namespace CodeSpace.Messages.Tasks.Trace;

/// <summary>
/// One raw row of a run's append-only event ledger (<c>workflow_run_record</c>) — the Trace tab's audit unit. Unlike
/// the narrative <c>RunTimelineEvent</c> (a filtered, human-titled story), this is the UNFILTERED machine truth: every
/// record type the engine wrote (run/node lifecycle, iteration, external-call, log, …) with its raw, already
/// secret-redacted <see cref="PayloadJson"/> verbatim. <see cref="RecordType"/> is an OPEN string — a reader renders an
/// unknown type generically, never switches exhaustively. <see cref="Sequence"/> is the per-run monotonic order.
/// </summary>
public sealed record RunRecordView
{
    public required long Sequence { get; init; }

    public required string RecordType { get; init; }

    public string? NodeId { get; init; }

    public required string IterationKey { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The record-type-specific payload, raw + already secret-redacted at write. Open shape — the Trace tab renders it as raw JSON.</summary>
    public required string PayloadJson { get; init; }

    /// <summary>Pairs an external_call <c>.started</c> with its <c>.completed</c>/<c>.failed</c> (null otherwise).</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>Self-link nesting a record under its parent (an attempt / external-call under its node) — null at top level.</summary>
    public Guid? ParentRecordId { get; init; }
}
