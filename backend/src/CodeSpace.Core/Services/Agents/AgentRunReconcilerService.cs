using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Jobs;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Recovers agent runs orphaned by a crashed worker / killed pod / rolling update — the "no-stuck-run"
/// guarantee for agents, mirroring the workflow engine's <c>StuckRunReconcilerService</c>. A run whose
/// worker vanished sits in <see cref="AgentRunStatus.Running"/> forever without this sweep; here it's
/// flipped to <see cref="AgentRunStatus.Failed"/> with an "abandoned" reason so the operator sees what
/// happened and can re-run.
///
/// <para>Liveness uses TWO signals (stronger than the workflow's ledger-only heuristic): the dedicated
/// <see cref="AgentRun.HeartbeatAt"/> ping AND live event activity. A run is abandoned only when BOTH
/// are quiet past the window — so a streaming agent that's still emitting events is never wrongly
/// killed even if its worker skipped a heartbeat.</para>
///
/// <para>Every transition is an atomic CAS (<c>WHERE status = Running</c>), so it's idempotent and safe
/// to run from multiple replicas, and it never tramples a worker that's completing the run right now.</para>
/// </summary>
public interface IAgentRunReconcilerService
{
    Task<AgentRunReconcileSummary> ReconcileAsync(CancellationToken cancellationToken);
}

public sealed class AgentRunReconcilerService : IAgentRunReconcilerService, IScopedDependency
{
    /// <summary>Operators tune reclaim aggressiveness via this env var (a TimeSpan, e.g. "00:05:00"); default 5 min. Pinned by a test (Rule 8). Forwards to <see cref="AgentRunLiveness.WindowEnvVar"/> so the abandonment window and the executor's heartbeat cadence share ONE source and can't drift.</summary>
    public const string LivenessWindowEnvVar = AgentRunLiveness.WindowEnvVar;

    /// <summary>Per-sweep cap so a backlog can't run a single tick forever.</summary>
    public const int BatchSize = 50;

    /// <summary>Operator-facing reason stamped on a reconciled run + appended to its log.</summary>
    public const string AbandonedError =
        "Agent run marked abandoned by the reconciler — the worker crashed or hung with no heartbeat or " +
        "event activity past the liveness window. Re-run the agent to retry; an interrupted run's " +
        "in-progress work is not resumed.";

    private readonly CodeSpaceDbContext _db;
    private readonly IAgentRunCompletionNotifier _notifier;
    private readonly ICodeSpaceBackgroundJobClient _jobs;
    private readonly ILogger<AgentRunReconcilerService> _logger;

    public AgentRunReconcilerService(CodeSpaceDbContext db, IAgentRunCompletionNotifier notifier, ICodeSpaceBackgroundJobClient jobs, ILogger<AgentRunReconcilerService> logger)
    {
        _db = db;
        _notifier = notifier;
        _jobs = jobs;
        _logger = logger;
    }

    public async Task<AgentRunReconcileSummary> ReconcileAsync(CancellationToken cancellationToken)
    {
        var marked = await MarkAbandonedRunningAsync(cancellationToken).ConfigureAwait(false);

        var (resumed, reDispatched) = await ReconcilePendingWaitsAsync(cancellationToken).ConfigureAwait(false);

        if (marked > 0 || resumed > 0 || reDispatched > 0)
            _logger.LogInformation("AgentRunReconciler: abandoned {Abandoned}, resumed {Resumed} stalled parent(s), re-dispatched {ReDispatched} stuck queued run(s)", marked, resumed, reDispatched);

        return new AgentRunReconcileSummary { MarkedAbandonedFromRunning = marked, ResumedStalledParents = resumed, ReDispatchedQueued = reDispatched };
    }

    private async Task<int> MarkAbandonedRunningAsync(CancellationToken cancellationToken)
    {
        var livenessThreshold = DateTimeOffset.UtcNow - AgentRunLiveness.Window;

        var candidates = await _db.AgentRun.AsNoTracking()
            .Where(r => r.Status == AgentRunStatus.Running
                        && (r.HeartbeatAt == null || r.HeartbeatAt < livenessThreshold)
                        && !_db.AgentRunEvent.Any(e => e.AgentRunId == r.Id && e.OccurredAt >= livenessThreshold))
            .OrderBy(r => r.HeartbeatAt)
            .Take(BatchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var marked = 0;
        foreach (var runId in candidates)
        {
            // Atomic CAS Running → Failed, pinned to status=Running so a worker completing the run
            // right now (its own Succeeded/Failed) wins the race and isn't overwritten.
            var transitioned = await _db.AgentRun
                .Where(r => r.Id == runId && r.Status == AgentRunStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, AgentRunStatus.Failed)
                    .SetProperty(r => r.Error, AbandonedError)
                    .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);

            if (transitioned == 0) continue;

            marked++;

            await TryAppendAbandonedEventAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        return marked;
    }

    /// <summary>
    /// The backstop for the agent → workflow hand-off: for every workflow run still parked on a pending
    /// <c>AgentRun</c> wait, unstick the agent run it's waiting on. A run that's already terminal but whose
    /// parent never resumed (a crashed worker, a reconciler-abandoned run, or an executor whose best-effort
    /// notify failed) → fire the completion notifier so the parent resumes. A run stuck <c>Queued</c> past
    /// the liveness window (its dispatch lost in the crash window between the parent committing Suspended
    /// and the executor being enqueued) → re-dispatch the executor; the claim guard makes a duplicate a
    /// no-op. Idempotent + retried every sweep (a resume flips the wait Resolved so it drops out next tick);
    /// a per-item failure is logged and retried, never aborting the sweep.
    /// </summary>
    private async Task<(int Resumed, int ReDispatched)> ReconcilePendingWaitsAsync(CancellationToken cancellationToken)
    {
        var waitingIds = await PendingAgentRunWaitIdsAsync(cancellationToken).ConfigureAwait(false);

        if (waitingIds.Count == 0) return (0, 0);

        var staleThreshold = DateTimeOffset.UtcNow - AgentRunLiveness.Window;

        var runs = await _db.AgentRun.AsNoTracking()
            .Where(r => waitingIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Status, r.CreatedDate })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var resumed = 0;
        foreach (var run in runs.Where(r => AgentRunStateMachine.IsTerminal(r.Status)))
            resumed += await TryResumeParentAsync(run.Id, run.Status, cancellationToken).ConfigureAwait(false);

        var reDispatched = 0;
        foreach (var run in runs.Where(r => r.Status == AgentRunStatus.Queued && r.CreatedDate < staleThreshold))
            reDispatched += TryReDispatch(run.Id, run.CreatedDate);

        return (resumed, reDispatched);
    }

    /// <summary>Resume the workflow parked on a terminal agent run, via the same notifier the executor uses. A failure is logged + retried next sweep — never throws out of the sweep.</summary>
    private async Task<int> TryResumeParentAsync(Guid runId, AgentRunStatus status, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.NotifyCompletedAsync(runId, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("AgentRunReconciler: resumed the workflow parked on terminal agent run {RunId} ({Status})", runId, status);
            return 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentRunReconciler: failed to resume the workflow parked on agent run {RunId}; will retry next sweep", runId);
            return 0;
        }
    }

    /// <summary>Re-enqueue the executor for a stuck-Queued run whose original dispatch was lost. The claim guard dedups a double-dispatch. A failure is logged + retried next sweep.</summary>
    private int TryReDispatch(Guid runId, DateTimeOffset createdDate)
    {
        try
        {
            _jobs.Enqueue<IAgentRunExecutor>(e => e.ExecuteAsync(runId, CancellationToken.None));

            _logger.LogInformation("AgentRunReconciler: re-dispatched stuck queued agent run {RunId} (created {CreatedDate:o})", runId, createdDate);
            return 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentRunReconciler: failed to re-dispatch queued agent run {RunId}; will retry next sweep", runId);
            return 0;
        }
    }

    /// <summary>The agent-run ids that workflow runs are currently parked on (pending AgentRun waits). The wait Token is the agent-run id; parse defensively.</summary>
    private async Task<List<Guid>> PendingAgentRunWaitIdsAsync(CancellationToken cancellationToken)
    {
        var tokens = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
            .Select(w => w.Token)
            .Take(BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return tokens
            .Select(t => Guid.TryParse(t, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }

    /// <summary>Append an Error event so the live log / replay timeline shows the abandonment. Best-effort — a logging failure doesn't undo the recovery.</summary>
    private async Task TryAppendAbandonedEventAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            _db.AgentRunEvent.Add(new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = runId, Kind = AgentEventKind.Error, Text = AbandonedError });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentRunReconciler: failed to append the abandoned-run event for {RunId}", runId);
        }
    }
}

/// <summary>Diagnostic summary of one reconcile sweep. Returned for log surfacing + the recurring-job result.</summary>
public sealed record AgentRunReconcileSummary
{
    /// <summary>Running runs flipped to Failed because their worker vanished (stale heartbeat + no events).</summary>
    public int MarkedAbandonedFromRunning { get; init; }

    /// <summary>Workflow runs resumed off a terminal agent run that hadn't propagated its completion (crash / failed notify).</summary>
    public int ResumedStalledParents { get; init; }

    /// <summary>Stuck-Queued agent runs whose dispatch was lost and were re-enqueued to the executor.</summary>
    public int ReDispatchedQueued { get; init; }

    public int Total => MarkedAbandonedFromRunning + ResumedStalledParents + ReDispatchedQueued;
}
