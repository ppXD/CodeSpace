using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One execution of a coding-agent harness — the durable, mutable lifecycle record an agent run is
/// tracked by. Mirrors <see cref="WorkflowRun"/> (status flips + lifecycle timestamps + xmin
/// concurrency), but for the harness-in-sandbox world rather than the node graph.
///
/// Status moves Queued → Running → Succeeded / Failed / Cancelled / TimedOut. <see cref="HeartbeatAt"/>
/// is the worker's liveness ping so a stuck-run reconciler can tell a crashed run from a slow one.
/// The full task envelope lives in <see cref="TaskJson"/> (so the envelope evolves without schema
/// churn); the normalized outcome lands in <see cref="ResultJson"/> on completion. The live event log
/// is a separate append-only table (B0.3b), not this row.
///
/// <see cref="WorkflowRunId"/> / <see cref="NodeId"/> link back to the agent.code node that spawned
/// this run; both are nullable so a future direct/standalone agent run is representable. The run-id
/// link is a soft cross-aggregate reference (no DB FK) — agent runs are managed independently of the
/// workflow-run lifecycle. <see cref="TeamId"/> is the denormalized team scope (FK to team, like
/// <see cref="WorkflowRun.TeamId"/>).
/// </summary>
public class AgentRun : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>The workflow run whose agent.code node spawned this. NULL for a standalone agent run. Soft link (no FK).</summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>The agent.code node id within that run. NULL for a standalone agent run.</summary>
    public string? NodeId { get; set; }

    /// <summary>Harness kind (e.g. "codex-cli"), denormalized for list/filtering. Also present inside <see cref="TaskJson"/>.</summary>
    public string Harness { get; set; } = default!;

    public AgentRunStatus Status { get; set; } = AgentRunStatus.Queued;

    /// <summary>Failure detail when <see cref="Status"/> is <see cref="AgentRunStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>The full <c>AgentTask</c> envelope as JSON — the run's inputs, kept whole so the envelope can evolve without a migration.</summary>
    public string TaskJson { get; set; } = "{}";

    /// <summary>The normalized <c>AgentRunResult</c> as JSON, written on completion. NULL while in-flight.</summary>
    public string? ResultJson { get; set; }

    /// <summary>Worker liveness ping; a stuck-Running reconciler reads this to recover crashed runs.</summary>
    public DateTimeOffset? HeartbeatAt { get; set; }

    /// <summary>
    /// DB-owned lease the claiming worker renews on every heartbeat (= now + the liveness Window). The
    /// reconciler reclaims a Running run whose lease has LAPSED — ground-truth liveness (a live worker keeps
    /// its lease fresh; the renew cadence is Window/3, so two pings can be lost before it lapses), rather than
    /// inferring death from heartbeat-silence. NULL until claimed (treated as lapsed, like a null heartbeat).
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>
    /// The durable runner handle (a <c>SandboxHandle</c> as JSON: runner kind, supervisor pid, spool
    /// directory, deadline) recorded the instant the run is launched on a durable runner — so a backend that
    /// restarts mid-run can re-attach to or recover the run from its spool instead of abandoning it. NULL
    /// until launched, and for runs on a non-durable runner.
    /// </summary>
    public string? RunnerHandleJson { get; set; }

    /// <summary>
    /// Monotonic fencing token, bumped on every claim (→ Running). A worker remembers the epoch it claimed
    /// with; completion requires it, so a worker whose run was reclaimed (a lease-expiry reclaim or a restart
    /// re-claim — each bumps the epoch) and then revived loses its terminal write rather than double-completing.
    /// Distinct from <see cref="Xmin"/>: xmin guards a single tracked save; this is an explicit CAS condition.
    /// </summary>
    public long FenceEpoch { get; set; }

    /// <summary>
    /// How many times the reconciler has re-claimed this run for a live re-attach (its detached process is
    /// alive but its worker vanished). Incremented in the SAME atomic UPDATE as each reclaim, so the count can
    /// never lag the action — once it reaches the reconciler's cap, a still-unattachable-but-alive run is
    /// abandoned instead of reclaimed forever (the no-livelock guarantee). 0 until first re-attached.
    /// </summary>
    public int ReattachAttempts { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>
    /// Npgsql xmin optimistic-concurrency token (same convention as <see cref="WorkflowRun.Xmin"/>):
    /// two workers can't both flip the same run Queued → Running — the loser gets
    /// DbUpdateConcurrencyException and backs off.
    /// </summary>
    public uint Xmin { get; set; }
}
