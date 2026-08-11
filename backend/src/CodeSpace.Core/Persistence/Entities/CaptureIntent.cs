using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// P2 (durable capture, slice 1) — one attempt's CAPTURE PROMISE: written when the harness exits, BEFORE any
/// capture side effect, and committed only after the capture sequence persisted its facts. Every capture step is
/// individually best-effort (a swallowed diff/offload/push/manifest failure leaves the run Succeeded), and the
/// crash-recovery spool path terminalizes with no capture at all — this row is what makes those windows VISIBLE:
/// <see cref="CaptureIntentStatus.Committed"/> = the sequence ran to its persist (including a CONFIRMED empty),
/// <see cref="CaptureIntentStatus.Indeterminate"/> = the attempt died mid-window (the work may or may not exist —
/// honest-unknown, the ToolCallLedger reaper's wording), <see cref="CaptureIntentStatus.Intended"/> = in flight.
/// One row per attempt: unique <c>(AgentRunId, FenceEpoch)</c> — a reclaimed re-attach runs at a bumped epoch and
/// makes its own promise.
/// </summary>
public class CaptureIntent : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Tenancy on EVERY row — the owning team (FK), like <see cref="ToolCallLedger.TeamId"/>.</summary>
    public Guid TeamId { get; set; }

    /// <summary>The agent run whose capture this promises. Soft link (no FK), like <see cref="ToolCallLedger.AgentRunId"/>.</summary>
    public Guid AgentRunId { get; set; }

    /// <summary>The owning workflow run when the attempt is workflow-bound — the completion protocol's join key. Soft link, nullable (standalone runs).</summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>The fence epoch this attempt claimed — the attempt discriminator (a reclaim bumps it) AND the commit guard: only the epoch that opened the promise may commit it.</summary>
    public long FenceEpoch { get; set; }

    public CaptureIntentStatus Status { get; set; } = CaptureIntentStatus.Intended;

    /// <summary>Intent-time facts (JSON): the repo cardinality the workspace materialized, the durable source handle (spool/workspace dir) — what a recovery pass would need to judge or resume the capture. Null-tolerant, additive.</summary>
    public string? ExpectationsJson { get; set; }

    /// <summary>Commit-time observation (JSON): what the capture actually persisted (changed-file count, patch artifact, branch, manifest presence) — including the explicit empty capture, which is a CONFIRMED fact here, never an absence.</summary>
    public string? FactsJson { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
