using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The durable, exactly-once, replayable record of ONE decision a supervisor emitted within a supervisor run — the
/// ledger ROW (named distinctly from any future model-facing decision DTO). Mirrors <see cref="ToolCallLedger"/>: the
/// unique <c>(SupervisorRunId, IdempotencyKey)</c> index is the exactly-once invariant — a racing duplicate INSERT hits
/// it and the loser reads the winner's row (the dedup path) instead of re-executing the decision. The key is
/// SERVER-derived (<c>decisionKind + ":" + SHA-256(canonical(payloadJson))</c> + a caller-supplied turn discriminator,
/// see <c>SupervisorDecisionLog.DeriveIdempotencyKey</c>) — NEVER read from the model — so a model cannot forge it to
/// replay an old decision or defeat dedup.
///
/// <para><see cref="TeamId"/> is on EVERY row (tenancy, FK to team like <see cref="AgentRun.TeamId"/>);
/// <see cref="SupervisorRunId"/> is a soft cross-aggregate link (no FK, like <see cref="AgentRunEvent.AgentRunId"/>) — the
/// ledger outlives its run row. <see cref="Sequence"/> is a per-run BIGSERIAL cursor giving the replay tape its natural
/// ordering. <see cref="PayloadJson"/> (the emitted decision) is FROZEN at insert (a JOURNAL field); <see cref="Status"/>
/// / <see cref="OutcomeJson"/> / <see cref="Error"/> are the deliberately-mutable CAS path (the execution result). The
/// DB-layer immutability trigger enforces that split.</para>
///
/// <para>Concurrency protection is FIRST-WRITER-WINS on <see cref="Status"/>, NOT epoch fencing (mirrors
/// <see cref="ToolCallLedger"/>): the single-winner guarantee comes from the status-guarded CAS transitions (the Pending
/// INSERT, the Pending → Running execution claim, then → terminal). <see cref="FenceEpoch"/> is RECORDED for
/// AUDIT/forensics only — a stale revived worker is fenced by LOSING the Status CAS, not by an epoch comparison.</para>
///
/// <para>This is PURE SUBSTRATE (E1): nothing writes the table until the supervisor node/loop wiring lands (E2). The
/// empty table existing is harmless.</para>
/// </summary>
public class SupervisorDecisionRecord : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Tenancy on EVERY row — the owning team (FK to team, like <see cref="AgentRun.TeamId"/>). Queries are team-scoped.</summary>
    public Guid TeamId { get; set; }

    /// <summary>The supervisor run this decision belongs to. Soft link (no FK), like <see cref="AgentRunEvent.AgentRunId"/>.</summary>
    public Guid SupervisorRunId { get; set; }

    /// <summary>Per-run BIGSERIAL cursor — the replay tape's natural ordering. DB-assigned on insert (value-generated).</summary>
    public long Sequence { get; set; }

    /// <summary>The kind of decision, e.g. "plan" / "spawn" / "retry" / "ask_human" / "merge" / "stop". OPEN string (stored as text) so a new decision kind adds zero schema churn.</summary>
    public string DecisionKind { get; set; } = default!;

    /// <summary>Server-derived at-most-once handle: <c>decisionKind:SHA-256(canonical(payloadJson))</c> (+ a caller turn discriminator). NEVER read from the model. Unique per <c>(SupervisorRunId, IdempotencyKey)</c>.</summary>
    public string IdempotencyKey { get; set; } = default!;

    /// <summary>Lower-case hex SHA-256 of the canonicalized payload (64 chars). The key already binds this, so a different payload is a different key.</summary>
    public string InputHash { get; set; } = default!;

    public SupervisorDecisionStatus Status { get; set; } = SupervisorDecisionStatus.Pending;

    /// <summary>The emitted decision — FROZEN at insert (a JOURNAL field the immutability trigger protects). The replay tape reads this verbatim.</summary>
    public string PayloadJson { get; set; } = default!;

    /// <summary>The execution result on a terminal success — the CAS-mutable result path. NULL while Pending / on a failure.</summary>
    public string? OutcomeJson { get; set; }

    /// <summary>Terminal failure reason. NULL on success / while Pending.</summary>
    public string? Error { get; set; }

    /// <summary>Mirrors <see cref="AgentRun.FenceEpoch"/> at claim time — recorded for audit/forensics only (NOT a CAS guard).</summary>
    public long FenceEpoch { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Npgsql xmin optimistic-concurrency token (same convention as <see cref="ToolCallLedger.Xmin"/>).</summary>
    public uint Xmin { get; set; }
}
