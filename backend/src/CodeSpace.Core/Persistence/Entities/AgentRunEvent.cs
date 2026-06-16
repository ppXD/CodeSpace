using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Append-only event log row for an <see cref="AgentRun"/> — the durable, ordered, replayable unit
/// behind the live "watch what the agent is doing right now" stream. One row per normalized
/// <see cref="AgentEvent"/> the harness emitted; <see cref="Sequence"/> + <see cref="OccurredAt"/>
/// are stamped on persist, so the harness stays free of persistence concerns. Mirrors
/// <see cref="WorkflowRunRecord"/> (the workflow engine's ledger): same BIGSERIAL-cursor +
/// run-scoped-index + append-only-trigger mechanics.
///
/// Append-only by contract: a DB-layer trigger rejects UPDATE/DELETE, so a consumer reading at
/// time T+1 sees exactly what the agent emitted at T. Audit columns are deliberately absent — this
/// IS the audit trail; <see cref="OccurredAt"/> is the only timestamp, and "by whom" lives on the
/// parent run.
/// </summary>
public class AgentRunEvent : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid AgentRunId { get; set; }

    /// <summary>
    /// Global monotonic insert-ordering token (Postgres BIGSERIAL). A live consumer tails one run via
    /// <c>WHERE agent_run_id = $1 AND sequence &gt; $cursor ORDER BY sequence</c> (the
    /// <c>idx_are_run_sequence</c> index); a global tail uses <c>sequence</c> alone. Strictly
    /// increasing within a run.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>Normalized event kind — the closed vocabulary every harness maps its native stream into (unknown → <see cref="AgentEventKind.Warning"/>). Stored as its string name.</summary>
    public AgentEventKind Kind { get; set; }

    /// <summary>Human-readable one-line rendering for the live stream.</summary>
    public string Text { get; set; } = default!;

    /// <summary>Optional structured payload (tool args, changed path, command, test counts, …) as JSON; NULL when the native event carried none OR when the payload was large enough to offload (then <see cref="DataArtifactId"/> holds the ref).</summary>
    public string? DataJson { get; set; }

    /// <summary>D2 #1 — when a large structured payload was offloaded to the artifact store, the artifact id holding the full JSON; <see cref="DataJson"/> is then NULL. NULL when the payload is inline (small) or absent.</summary>
    public Guid? DataArtifactId { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public AgentRun Run { get; set; } = default!;
}
