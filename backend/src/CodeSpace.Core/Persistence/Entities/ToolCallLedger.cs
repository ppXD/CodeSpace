using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The durable exactly-once + audit record of ONE side-effecting MCP tool call within an agent run. The unique
/// <c>(AgentRunId, IdempotencyKey)</c> index is the exactly-once invariant: a racing duplicate INSERT hits it and
/// the loser reads the winner's row (the dedup path) instead of re-running the side effect. The key is SERVER-derived
/// (<c>toolKind + SHA-256(canonical(input))</c>, see <c>ToolCallKey</c>) — never read from the wire — so a model
/// cannot forge it to replay an old success or defeat dedup. Read-only tools are NOT tracked here (no side effect to
/// dedup); only side-effecting tools get a row.
///
/// <para><see cref="TeamId"/> is on EVERY row (tenancy, FK to team like <see cref="AgentRun.TeamId"/>);
/// <see cref="AgentRunId"/> is a soft cross-aggregate link (no FK, like <see cref="AgentRunEvent.AgentRunId"/>).
/// <see cref="ResultJson"/> / <see cref="Error"/> store the ALREADY-REDACTED tool result — the row is itself a leak
/// surface, so the handler redacts BEFORE persisting. <see cref="FenceEpoch"/> mirrors the run's epoch at record
/// time, so a reclaimed-then-revived worker's terminal write loses the CAS (same discipline as
/// <see cref="AgentRun.FenceEpoch"/>).</para>
///
/// <para>The approval columns (<see cref="ApprovalMessageId"/> / <see cref="ApprovalToken"/> /
/// <see cref="ApprovalDeadlineAt"/>) + the <c>AwaitingApproval</c> / <c>Expired</c> statuses are reserved for item D
/// (durable mid-turn HITL); they are nullable + unused by the C ledger vertical (additive, no behavior).</para>
/// </summary>
public class ToolCallLedger : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Tenancy on EVERY row — the owning team, stamped from the handler's run team (FK to team, like <see cref="AgentRun.TeamId"/>). Queries are team-scoped.</summary>
    public Guid TeamId { get; set; }

    /// <summary>The agent run this call belongs to. Soft link (no FK), like <see cref="AgentRunEvent.AgentRunId"/>.</summary>
    public Guid AgentRunId { get; set; }

    /// <summary><c>IAgentTool.Kind</c>, e.g. "git.open_pr" — the tool that was called.</summary>
    public string ToolKind { get; set; } = default!;

    /// <summary>Server-derived at-most-once handle: <c>toolKind:SHA-256(canonical(input))</c> (see <c>ToolCallKey</c>). NEVER read from the wire — a model-supplied key is a forgery surface. Unique per <c>(AgentRunId, IdempotencyKey)</c>.</summary>
    public string IdempotencyKey { get; set; } = default!;

    /// <summary>Lower-case hex SHA-256 of the canonicalized input (64 chars). The key already binds this, so a different input is a different key — never silently collapsed.</summary>
    public string InputHash { get; set; } = default!;

    public ToolCallLedgerStatus Status { get; set; } = ToolCallLedgerStatus.Pending;

    /// <summary>The ALREADY-REDACTED tool-result content on a terminal success. NULL while Pending / on a failure.</summary>
    public string? ResultJson { get; set; }

    /// <summary>Terminal failure / denial reason (already redacted). NULL on success / while Pending.</summary>
    public string? Error { get; set; }

    // ── Durable HITL (item D) — null on a non-approval call; populated when Status == AwaitingApproval. Unused by C. ──

    /// <summary>The interaction-card message surfacing this call for approval (item D).</summary>
    public Guid? ApprovalMessageId { get; set; }

    /// <summary>Server-side bearer the respond path matches on (item D) — never surfaced to a client.</summary>
    public string? ApprovalToken { get; set; }

    /// <summary>When the approval expires (item D).</summary>
    public DateTimeOffset? ApprovalDeadlineAt { get; set; }

    /// <summary>Mirrors <see cref="AgentRun.FenceEpoch"/> at record time — a reclaimed-then-revived worker's terminal CAS is fenced out (item D resume).</summary>
    public long FenceEpoch { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Npgsql xmin optimistic-concurrency token (same convention as <see cref="AgentRun.Xmin"/>).</summary>
    public uint Xmin { get; set; }
}
