using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Owns the durable lifecycle of an agent run: create it (Queued), flip it Running, append normalized
/// live-log events, heartbeat for liveness, and land a terminal result. The one place that reads/writes
/// <see cref="AgentRun"/> + <see cref="AgentRunEvent"/>, so a node, a worker, a reconciler, and the API
/// all drive runs through the same guarded transitions — no status flip bypasses
/// <see cref="AgentRunStateMachine"/>, and the run's xmin token stops two workers double-flipping it.
/// </summary>
public interface IAgentRunService
{
    /// <summary>Persist a new run in <see cref="AgentRunStatus.Queued"/> with <paramref name="task"/> as its envelope. workflowRunId/nodeId soft-link the spawning agent.code node (null for a standalone run).</summary>
    Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, CancellationToken cancellationToken);

    /// <summary>
    /// Queued → Running; stamps StartedAt + an initial heartbeat, BUMPS the fencing epoch, and returns the new
    /// epoch — the claimer must complete under it (see the epoch overload of <see cref="CompleteAsync(Guid,AgentRunResult,long,CancellationToken)"/>),
    /// so a later reclaim (which bumps the epoch again) fences this claimer out. Throws
    /// <see cref="AgentRunTransitionException"/> when the run isn't Queued.
    /// </summary>
    Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Refresh the liveness heartbeat a stuck-run reconciler reads. Idempotent; does not change status.</summary>
    Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-claim a still-<see cref="AgentRunStatus.Running"/> run for a fresh observer to re-attach to (its
    /// durable process is alive but its worker vanished): atomically BUMP the fencing epoch (so a revived
    /// original observer's epoch-fenced completion loses), INCREMENT the re-attach attempt counter (so the
    /// reconciler's ceiling can never lag the action — it's written in this same transaction), and stamp a FRESH
    /// lease + heartbeat (so the run drops out of the reconciler's stale-candidate set until the re-attaching
    /// worker renews it — no duplicate re-dispatch). Status-guarded CAS like <see cref="CompleteAsync(Guid,AgentRunResult,long,CancellationToken)"/>
    /// (a pure UPDATE, never a tracked save — so it can't strand a long run on optimistic concurrency). Returns
    /// whether it won the row (0 rows = no longer Running / another replica won → don't re-dispatch).
    /// </summary>
    Task<bool> ReclaimForReattachAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Record the run's durable runner handle (a <c>SandboxHandle</c> as JSON: pid + spool location +
    /// deadline) the instant it's launched, so a restarted backend can re-attach to or recover it from the
    /// spool rather than abandoning it. Idempotent set-based UPDATE (like <see cref="HeartbeatAsync"/>); does
    /// not change status.
    /// </summary>
    Task SetRunnerHandleAsync(Guid runId, string handleJson, CancellationToken cancellationToken);

    /// <summary>Append one normalized event to the run's append-only log. Sequence + timestamp are DB-assigned.</summary>
    Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken);

    /// <summary>Land a terminal result (the target state is <paramref name="result"/>'s Status); stores the result + CompletedAt. Throws when the transition is illegal or the result's status isn't terminal.</summary>
    Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken);

    /// <summary>
    /// Cancel a run that is still <see cref="AgentRunStatus.Queued"/> (staged but never launched) — the
    /// no-orphan path for a branch agent run whose parent workflow run reached a terminal state before its
    /// dispatch ran. A status-guarded CAS pinned to <c>Queued</c> (a pure UPDATE, like <see cref="HeartbeatAsync"/>),
    /// so it never races a worker that just claimed the run (that CAS already moved it to Running and loses 0
    /// rows here). Idempotent + safe from multiple replicas; <paramref name="reason"/> is stamped as the run's
    /// Error. Returns whether it won the row (false = already launched / already terminal → leave it alone).
    /// </summary>
    Task<bool> CancelQueuedAsync(Guid runId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="CompleteAsync(Guid,AgentRunResult,CancellationToken)"/>, but ALSO fenced on
    /// <paramref name="expectedEpoch"/> — the epoch the caller claimed with (<see cref="MarkRunningAsync"/>).
    /// The status-guarded CAS additionally requires the run still carries that epoch, so a worker whose run was
    /// reclaimed (its epoch bumped) and then revived matches 0 rows and throws, rather than double-completing.
    /// The executor uses this; an unfenced caller (a test, a direct admin path) uses the 3-arg overload.
    /// </summary>
    Task CompleteAsync(Guid runId, AgentRunResult result, long expectedEpoch, CancellationToken cancellationToken);

    /// <summary>
    /// Read a run by id (untracked) — the SYSTEM/execution path (the executor loads any staged run to run
    /// it, regardless of team), so it is intentionally NOT team-scoped. Throws
    /// <see cref="KeyNotFoundException"/> when absent. An operator-facing read must use a team-scoped query.
    /// </summary>
    Task<AgentRun> GetAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// TEAM-SCOPED operator read of a run's live status (null when the run isn't <paramref name="teamId"/>'s,
    /// so a foreign id leaks nothing) — distinct from <see cref="GetAsync"/>, which is the un-scoped execution
    /// path. Backs the run-detail's live status header.
    /// </summary>
    Task<AgentRunSummary?> GetSummaryForTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Read the run's events with <c>Sequence &gt; afterSequence</c> in order — the operator-facing
    /// live-stream / replay cursor (pass 0 for the whole log). TEAM-SCOPED: returns an empty list when the
    /// run doesn't belong to <paramref name="teamId"/>, so a foreign run id leaks neither events nor its
    /// existence.
    /// </summary>
    Task<IReadOnlyList<AgentRunEvent>> GetEventsAsync(Guid runId, Guid teamId, long afterSequence, CancellationToken cancellationToken);
}

public sealed class AgentRunService : IAgentRunService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<AgentRunService> _logger;

    public AgentRunService(CodeSpaceDbContext db, ILogger<AgentRunService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, CancellationToken cancellationToken)
    {
        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = workflowRunId,
            NodeId = nodeId,
            Harness = task.Harness,
            Status = AgentRunStatus.Queued,
            TaskJson = JsonSerializer.Serialize(task, AgentJson.Options),
        };

        _db.AgentRun.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Agent run created. RunId={RunId} Harness={Harness} TeamId={TeamId}", run.Id, run.Harness, teamId);

        return run;
    }

    public async Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await LoadAsync(runId, cancellationToken).ConfigureAwait(false);

        EnsureTransition(run, AgentRunStatus.Running);

        run.Status = AgentRunStatus.Running;
        run.StartedAt = DateTimeOffset.UtcNow;
        run.HeartbeatAt = DateTimeOffset.UtcNow;
        run.LeaseExpiresAt = AgentRunLiveness.NextLeaseExpiry();   // start the lease; the heartbeat renews it
        run.FenceEpoch += 1;   // bump on claim; the claimer completes under this epoch, so a later reclaim fences it out

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return run.FenceEpoch;
    }

    public async Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Tracking-free set-based UPDATE (like the reconciler's CAS). The executor pings this on a loop over
        // a long-lived scope, so a load-mutate-save would keep a tracked entity + a stale xmin between pings —
        // one lost optimistic-concurrency round would then silently kill every later heartbeat. A pure UPDATE
        // never participates in optimistic concurrency; a missing row is a harmless 0-row no-op.
        //
        // Renews the lease alongside the heartbeat: a live worker pushes lease_expires_at forward every ping,
        // so the reconciler's lease-expiry reclaim only fires once the worker stops pinging (it died/hung).
        var now = DateTimeOffset.UtcNow;
        await _db.AgentRun
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.HeartbeatAt, (DateTimeOffset?)now)
                .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)(now + AgentRunLiveness.LeaseDuration)), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ReclaimForReattachAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Tracking-free status-guarded CAS (like CompleteCoreAsync, NOT MarkRunningAsync's tracked load-save — a
        // tracked save would attach a stale-xmin entity to the reconciler's shared DbContext and could fail
        // optimistic concurrency against the executor's concurrent heartbeat). Atomic in-DB increment of the epoch
        // (the column-expression SetProperty form) fences a revived original observer; the fresh lease + heartbeat
        // take the run out of the stale sweep until the re-attaching worker keeps renewing it. 0 rows = the run is
        // no longer Running (another replica reclaimed it, or it already landed terminal) → caller skips dispatch.
        var now = DateTimeOffset.UtcNow;
        var reclaimed = await _db.AgentRun
            .Where(r => r.Id == runId && r.Status == AgentRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.FenceEpoch, r => r.FenceEpoch + 1)
                .SetProperty(r => r.ReattachAttempts, r => r.ReattachAttempts + 1)
                .SetProperty(r => r.HeartbeatAt, (DateTimeOffset?)now)
                .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)(now + AgentRunLiveness.LeaseDuration)), cancellationToken)
            .ConfigureAwait(false);

        return reclaimed == 1;
    }

    public async Task SetRunnerHandleAsync(Guid runId, string handleJson, CancellationToken cancellationToken)
    {
        // Tracking-free set-based UPDATE (like HeartbeatAsync): a pure UPDATE that never participates in
        // optimistic concurrency. Safe to bump the row independently of the executor's tracked instance because
        // CompleteAsync flips status via a status-guarded CAS (not the xmin token), so neither this write nor the
        // heartbeat's pings can block completion. A missing row is a harmless no-op.
        await _db.AgentRun
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RunnerHandleJson, handleJson), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken)
    {
        var record = new AgentRunEvent
        {
            Id = Guid.NewGuid(),
            AgentRunId = runId,
            Kind = @event.Kind,
            Text = @event.Text,
            DataJson = @event.Data?.GetRawText(),
        };

        _db.AgentRunEvent.Add(record);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return record;
    }

    public Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken) =>
        CompleteCoreAsync(runId, result, expectedEpoch: null, cancellationToken);

    public Task CompleteAsync(Guid runId, AgentRunResult result, long expectedEpoch, CancellationToken cancellationToken) =>
        CompleteCoreAsync(runId, result, expectedEpoch, cancellationToken);

    private async Task CompleteCoreAsync(Guid runId, AgentRunResult result, long? expectedEpoch, CancellationToken cancellationToken)
    {
        if (!AgentRunStateMachine.IsTerminal(result.Status))
            throw new AgentRunTransitionException($"AgentRunResult.Status must be terminal — got {result.Status}.");

        // Read the current status FRESH + untracked, then flip via a status-guarded CAS — NOT a tracked save on
        // the xmin token. The worker heartbeats its own run on a separate DbContext (HeartbeatAsync), bumping the
        // row's xmin every interval; a tracked Complete would then fail its optimistic-concurrency check on any
        // run that outlived one heartbeat (~window/3) and never land terminal — stranding it Running until the
        // reconciler abandoned it. Guarding on STATUS instead means the worker's own liveness pings can't block
        // its completion, while a genuine concurrent transition (the reconciler abandoning this run) still wins
        // the CAS and this side loses cleanly. Mirrors the reconciler's idempotent CAS.
        //
        // When expectedEpoch is supplied (the executor's claim epoch), the CAS ALSO requires the run still
        // carries that epoch — so a worker whose run was reclaimed (the reclaim bumps fence_epoch) and then
        // revived matches 0 rows and loses, instead of double-completing. A null epoch (an unfenced caller)
        // keeps the status-only guard.
        var current = await _db.AgentRun.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => (AgentRunStatus?)r.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");

        if (!AgentRunStateMachine.IsLegalTransition(current, result.Status))
            throw new AgentRunTransitionException($"Illegal AgentRun transition {current} → {result.Status} (run {runId}).");

        var resultJson = JsonSerializer.Serialize(result, AgentJson.Options);

        var flipped = await _db.AgentRun
            .Where(r => r.Id == runId && r.Status == current && (expectedEpoch == null || r.FenceEpoch == expectedEpoch))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, result.Status)
                .SetProperty(r => r.ResultJson, resultJson)
                .SetProperty(r => r.Error, result.Error)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (flipped == 0)
            throw new AgentRunTransitionException($"AgentRun {runId} was no longer {current}{(expectedEpoch is { } e ? $" at epoch {e}" : "")} at completion — a concurrent transition or reclaim won the race.");

        _logger.LogInformation("Agent run completed. RunId={RunId} Status={Status}", runId, result.Status);
    }

    public async Task<bool> CancelQueuedAsync(Guid runId, string reason, CancellationToken cancellationToken)
    {
        // Tracking-free status-guarded CAS pinned to Queued (mirrors AbandonAsync's Running-guarded flip): a
        // pure UPDATE that never participates in optimistic concurrency. Queued → Cancelled is legal
        // (AgentRunStateMachine). If a worker already claimed the run (Queued → Running), this matches 0 rows
        // and loses cleanly — we never trample a run that's mid-flight. 0 rows = already launched / terminal.
        var cancelled = await _db.AgentRun
            .Where(r => r.Id == runId && r.Status == AgentRunStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AgentRunStatus.Cancelled)
                .SetProperty(r => r.Error, reason)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (cancelled == 0) return false;

        _logger.LogInformation("Agent run cancelled while still queued. RunId={RunId} Reason={Reason}", runId, reason);
        return true;
    }

    public async Task<AgentRun> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.AgentRun.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");

    public async Task<AgentRunSummary?> GetSummaryForTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _db.AgentRun.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == runId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        return run is null ? null : new AgentRunSummary
        {
            Id = run.Id,
            Status = run.Status,
            Harness = run.Harness,
            Error = run.Error,
            StartedAt = run.StartedAt,
            HeartbeatAt = run.HeartbeatAt,
            CompletedAt = run.CompletedAt,
            CreatedDate = run.CreatedDate,
        };
    }

    public async Task<IReadOnlyList<AgentRunEvent>> GetEventsAsync(Guid runId, Guid teamId, long afterSequence, CancellationToken cancellationToken)
    {
        // Gate on ownership first so a foreign run id leaks neither events nor existence (return empty,
        // never throw "not found" only for owned runs — both cross-team and absent look identical).
        var owned = await _db.AgentRun.AsNoTracking()
            .AnyAsync(r => r.Id == runId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        if (!owned) return Array.Empty<AgentRunEvent>();

        return await _db.AgentRunEvent.AsNoTracking()
            .Where(e => e.AgentRunId == runId && e.Sequence > afterSequence)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentRun> LoadAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.AgentRun.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");

    private static void EnsureTransition(AgentRun run, AgentRunStatus to)
    {
        if (!AgentRunStateMachine.IsLegalTransition(run.Status, to))
            throw new AgentRunTransitionException($"Illegal AgentRun transition {run.Status} → {to} (run {run.Id}).");
    }
}

/// <summary>An agent run was asked to make a transition its lifecycle doesn't allow (completing a Queued run, re-running a terminal one, a non-terminal completion status, …). A node/handler surfaces this as a clean failure.</summary>
public sealed class AgentRunTransitionException : Exception
{
    public AgentRunTransitionException(string message) : base(message) { }
}
