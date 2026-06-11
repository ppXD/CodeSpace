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

    /// <summary>Queued → Running; stamps StartedAt + an initial heartbeat. Throws <see cref="AgentRunTransitionException"/> when the run isn't Queued.</summary>
    Task MarkRunningAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Refresh the liveness heartbeat a stuck-run reconciler reads. Idempotent; does not change status.</summary>
    Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken);

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

    public async Task MarkRunningAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await LoadAsync(runId, cancellationToken).ConfigureAwait(false);

        EnsureTransition(run, AgentRunStatus.Running);

        run.Status = AgentRunStatus.Running;
        run.StartedAt = DateTimeOffset.UtcNow;
        run.HeartbeatAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Tracking-free set-based UPDATE (like the reconciler's CAS). The executor pings this on a loop over
        // a long-lived scope, so a load-mutate-save would keep a tracked entity + a stale xmin between pings —
        // one lost optimistic-concurrency round would then silently kill every later heartbeat. A pure UPDATE
        // never participates in optimistic concurrency; a missing row is a harmless 0-row no-op.
        await _db.AgentRun
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.HeartbeatAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);
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

    public async Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken)
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
        var current = await _db.AgentRun.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => (AgentRunStatus?)r.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");

        if (!AgentRunStateMachine.IsLegalTransition(current, result.Status))
            throw new AgentRunTransitionException($"Illegal AgentRun transition {current} → {result.Status} (run {runId}).");

        var resultJson = JsonSerializer.Serialize(result, AgentJson.Options);

        var flipped = await _db.AgentRun
            .Where(r => r.Id == runId && r.Status == current)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, result.Status)
                .SetProperty(r => r.ResultJson, resultJson)
                .SetProperty(r => r.Error, result.Error)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (flipped == 0)
            throw new AgentRunTransitionException($"AgentRun {runId} was no longer {current} at completion — a concurrent transition won the race.");

        _logger.LogInformation("Agent run completed. RunId={RunId} Status={Status}", runId, result.Status);
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
