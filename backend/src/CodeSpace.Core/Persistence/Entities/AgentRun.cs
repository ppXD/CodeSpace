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
/// <see cref="WorkflowRunId"/> / <see cref="NodeId"/> link back to the agent.run node that spawned
/// this run; both are nullable so a future direct/standalone agent run is representable. The run-id
/// link is a soft cross-aggregate reference (no DB FK) — agent runs are managed independently of the
/// workflow-run lifecycle. <see cref="TeamId"/> is the denormalized team scope (FK to team, like
/// <see cref="WorkflowRun.TeamId"/>).
/// </summary>
public class AgentRun : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>The workflow run whose agent.run node spawned this. NULL for a standalone agent run. Soft link (no FK).</summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>
    /// The persona this agent run embodies — promoted from <c>task_jsonb.agentDefinitionId</c> to a column so the runs
    /// index can filter "runs that used agent X" (an EXISTS over agent_run). NULL when the task carries no persona (a
    /// raw harness task). Set at creation from the task's <c>AgentDefinitionId</c>.
    /// </summary>
    public Guid? AgentDefinitionId { get; set; }

    /// <summary>The agent.run node id within that run. NULL for a standalone agent run.</summary>
    public string? NodeId { get; set; }

    /// <summary>
    /// The owning workflow CELL's iteration key — the same value the engine stamps on the spawning node's
    /// <c>workflow_run_node</c> / <c>workflow_run_wait</c> row, so this run is addressable by the full
    /// <c>(WorkflowRunId, NodeId, IterationKey)</c> cell rather than just the node. Empty string for a
    /// top-level node, a standalone run, or the non-container case (the engine's <c>NoIteration</c> convention).
    /// For a map branch / loop iteration it is <c>&lt;nodeId&gt;#&lt;index&gt;</c> (nested keys combine); for a
    /// supervisor turn-spawn it is the turn cell <c>&lt;nodeId&gt;#turn{N}</c>. The D4 correlation spine — N
    /// agent branches under one map node are now distinguishable, and a future from-cell rerun can target one
    /// branch's agent run.
    /// </summary>
    public string IterationKey { get; set; } = "";

    /// <summary>Harness kind (e.g. "codex-cli"), denormalized for list/filtering. Also present inside <see cref="TaskJson"/>.</summary>
    public string Harness { get; set; } = default!;

    public AgentRunStatus Status { get; set; } = AgentRunStatus.Queued;

    /// <summary>Failure detail when <see cref="Status"/> is <see cref="AgentRunStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>The full <c>AgentTask</c> envelope as JSON — the run's inputs, kept whole so the envelope can evolve without a migration.</summary>
    public string TaskJson { get; set; } = "{}";

    /// <summary>The normalized <c>AgentRunResult</c> as JSON, written on completion. NULL while in-flight.</summary>
    public string? ResultJson { get; set; }

    /// <summary>
    /// P3.1a: the harness-native session/thread id captured off the run's CLI conversation (Claude's
    /// <c>session_id</c>, Codex's <c>thread_id</c>) — promoted from <c>result_jsonb</c> to a first-class column so a
    /// rerun's CONTINUE lookup is a column read, not a JSON probe. NULL for a run whose stream carried no session id
    /// (a pre-session CLI). Set on completion from <c>AgentRunResult.SessionId</c>, and — since 3c — already at the
    /// run's first session-transcript checkpoint, because a checkpoint nothing can ADDRESS is not resumable: the
    /// continuation needs this id to hand the CLI its <c>--resume</c>. Every reader that treats a non-null id as
    /// "resumable" also requires a transcript out of <see cref="ResultJson"/> (<c>TryResumable</c>, both-or-neither),
    /// so an in-flight row carrying one is skipped exactly as a null one was.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// 3c: the artifact holding this run's resumable session transcript as of its most recent MID-RUN checkpoint —
    /// the only thing a run leaves behind that another host can continue from after the launching host dies. NULL
    /// until the first checkpoint, and for every run whose harness has no addressable session transcript. A soft link
    /// to <c>workflow_artifact.id</c>, and it is exactly that: the reference the artifact reaper's oracle probes
    /// (<c>ArtifactReferenceOracle.ReferenceSites</c>) so a live checkpoint is never collected.
    /// </summary>
    public Guid? SessionTranscriptCheckpointArtifactId { get; set; }

    /// <summary>When <see cref="SessionTranscriptCheckpointArtifactId"/> was taken. The cadence clock for the next checkpoint, and the age an operator (or a resumed attempt) reads to know how much of the conversation survived the lost host.</summary>
    public DateTimeOffset? SessionTranscriptCheckpointAt { get; set; }

    /// <summary>
    /// 3c: the abandoned run this one was STAGED to continue from a session checkpoint — the structured link, so
    /// "which attempt took over from which" is a column rather than a sentence buried in an event. Soft link (no
    /// FK), like every other agent-run cross-reference. NULL for every ordinary run.
    ///
    /// <para>Staged, not necessarily restored: this is written when the row is created, and whether the checkpoint
    /// could actually be READ is only knowable at launch (a reaped blob, an unreachable destination — then the
    /// attempt runs cold and says so). The honest reading of a non-null value is therefore "this attempt succeeded
    /// that one after it lost its host", which is true either way; what was recovered is the confinement record's
    /// <c>ResumedFromCheckpointAt</c>, which the degrade clears.</para>
    /// </summary>
    public Guid? ResumedFromAgentRunId { get; set; }

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

    /// <summary>Durable count of failed terminal-spool cleanup attempts for the current runner handle.</summary>
    public int SpoolCleanupAttempts { get; set; }

    /// <summary>Database time at which the reaper last failed to clean the current runner handle.</summary>
    public DateTimeOffset? SpoolCleanupLastAttemptAt { get; set; }

    /// <summary>Database-owned eligibility boundary for retrying a failed terminal-spool cleanup.</summary>
    public DateTimeOffset? SpoolCleanupNextAttemptAt { get; set; }

    /// <summary>Bounded machine-readable reason for the most recent failed cleanup attempt.</summary>
    public string? SpoolCleanupLastErrorCode { get; set; }

    /// <summary>
    /// What confinement the launch ACTUALLY applied (a <c>SandboxConfinement</c> as JSON: outcome, the reason the
    /// host could not confine, and whether egress was severed), stamped once at launch. Its own column rather than a
    /// field of <see cref="RunnerHandleJson"/> because the spool reaper nulls that handle 24h after the run goes
    /// terminal, and the posture a run had must outlive its recovery aid. NULL for a run launched before this
    /// existed — a reader with no record states the old hedged posture rather than guessing an enforced one.
    /// </summary>
    public string? SandboxConfinementJson { get; set; }

    /// <summary>
    /// Monotonic fencing token, bumped on every claim (→ Running). A worker remembers the epoch it claimed
    /// with; completion requires it, so a worker whose run was reclaimed (a lease-expiry reclaim or a restart
    /// re-claim — each bumps the epoch) and then revived loses its terminal write rather than double-completing.
    /// Distinct from <see cref="Xmin"/>: xmin guards a single tracked save; this is an explicit CAS condition.
    /// </summary>
    public long FenceEpoch { get; set; }

    /// <summary>The activated observer, never the logical task or physical process identity. Null on legacy rows or while a reconciler reservation awaits activation.</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>Server-minted, one-time dispatch reservation. Retained after activation to resolve an ambiguous acknowledgement against the exact locally minted owner.</summary>
    public Guid? ReattachReservationId { get; set; }

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
