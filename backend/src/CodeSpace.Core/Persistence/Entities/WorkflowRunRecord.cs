namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Append-only lifecycle ledger row. Every interesting event during a <see cref="WorkflowRun"/>
/// lands here: node started/completed/failed/skipped, iteration boundaries, external API calls
/// (request + response paired by <see cref="CorrelationId"/>), activity log lines.
/// <see cref="RecordType"/> is an open string so new event kinds add zero schema churn — see
/// <c>WorkflowRunRecordTypes</c> for the canonical set.
///
/// Append-only by contract: a DB-layer trigger rejects UPDATE/DELETE on existing rows. To
/// represent state change (a retry, a status flip), the engine INSERTs another record with
/// a later sequence. Consumers project the latest-state per (run, node, iter) via the
/// <c>workflow_run_node</c> view.
///
/// Audit columns are deliberately absent — this IS the audit trail. <see cref="OccurredAt"/>
/// is the only timestamp needed; the "by whom" lives on the upstream <c>workflow_run_request</c>.
/// </summary>
public class WorkflowRunRecord : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>
    /// Insert-ordering token, backed by a single PostgreSQL BIGSERIAL sequence shared across
    /// every <c>workflow_run_record</c> row in the database. The sequence is therefore
    /// <b>globally</b> monotonic — strict across runs, not just within one. Consumers MUST
    /// scope queries by <c>run_id</c> when they want per-run ordering (the index
    /// <c>idx_wrr_run_sequence</c> covers this).
    ///
    /// <para>Global monotonicity is a feature, not an accident: future ledger-streaming
    /// consumers can use Sequence as a single global cursor (<c>WHERE sequence > $last_seen
    /// ORDER BY sequence</c>) and still see writes in the same order the engine emitted
    /// them, across all runs. Within a single run, monotonicity holds trivially.</para>
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Open-string event discriminator. Examples: <c>node.started</c>, <c>node.completed</c>,
    /// <c>node.failed</c>, <c>external_call.started</c>, <c>iteration.completed</c>, <c>log</c>.
    /// See <c>WorkflowRunRecordTypes</c> for the canonical engine-emitted set. Plugin authors
    /// may emit their own dotted-namespace types (e.g. <c>llm.token</c>) without engine churn.
    /// </summary>
    public string RecordType { get; set; } = default!;

    /// <summary>The node this event pertains to. NULL for run-level events.</summary>
    public string? NodeId { get; set; }

    /// <summary>Iteration index for nodes inside flow.iterate. Empty string for non-iteration nodes.</summary>
    public string IterationKey { get; set; } = string.Empty;

    /// <summary>
    /// Groups related records — e.g. <c>external_call.started</c> + <c>external_call.completed</c>
    /// share a correlation id so the UI can pair request with response. NULL when no grouping
    /// relationship applies.
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Self-FK for hierarchical records (an attempt nested under its parent node row, a
    /// retry chain). Most records leave this NULL.
    /// </summary>
    public Guid? ParentRecordId { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Record-type-specific shape, serialised as JSON. Canonical payloads per record_type
    /// are documented on the migration file (0015_run_record_ledger.sql) — keep them in lockstep.
    /// </summary>
    public string PayloadJson { get; set; } = "{}";

    public WorkflowRun Run { get; set; } = default!;
}
