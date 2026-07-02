using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Reconciliation;

/// <summary>
/// Four independent sweeps, one per stuck-state class. Order matters less than you'd think
/// — each sweep's CAS protects against racing a normal flow or another reconciler tick.
/// </summary>
public sealed class StuckRunReconcilerService : IStuckRunReconcilerService, IScopedDependency
{
    /// <summary>Threshold for "Pending too long" — past this, re-dispatch.</summary>
    public static readonly TimeSpan PendingStuckAfter = TimeSpan.FromMinutes(2);

    /// <summary>Threshold for "Enqueued but no worker picked up" — past this, revert to Pending.</summary>
    public static readonly TimeSpan EnqueuedStuckAfter = TimeSpan.FromMinutes(10);

    /// <summary>Threshold for "Running but no progress" — past this AND no recent ledger activity, mark Failure.</summary>
    public static readonly TimeSpan RunningStuckAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Threshold for "Suspended but stranded" — past this AND zero pending waits, re-dispatch.
    /// <para>2 minutes is safe because the predicate ALSO requires zero pending waits: every
    /// legitimately-parked run — a human approval/action awaited for hours, a timer/delay, a
    /// freshly-suspended map with K branch waits — HAS at least one Pending wait and is excluded
    /// outright, regardless of age. The only durable zero-pending-wait Suspended state is the
    /// stranded one (a resume resolved its last wait but its Suspended→Pending flip no-op'd
    /// against an in-flight re-walk that re-suspended the run). The threshold's sole job is to
    /// clear the sub-second resolve-then-flip window during a NORMAL last-wait resume — for that
    /// fleeting instant the run is Suspended with zero pending waits, but a concurrent flip is
    /// about to drive it. We measure age from <see cref="WorkflowRun.LastModifiedDate"/>: the
    /// engine flips a run to Suspended via a TRACKED <c>run.Status = Suspended</c> + SaveChanges
    /// (which the audit hook stamps) AFTER every branch wait is already persisted Pending, so a
    /// freshly-suspended run always carries a FRESH LastModifiedDate. 2 min dwarfs the
    /// sub-second window without making real recovery latency noticeable.</para>
    /// </summary>
    public static readonly TimeSpan SuspendedStrandedAfter = TimeSpan.FromMinutes(2);

    /// <summary>"Recent" ledger activity window — if a run has emitted records within this window, treat it as alive.</summary>
    public static readonly TimeSpan LedgerLivenessWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Threshold for "supervisor self-advance lost" (PR-E E2) — a run Suspended past this with a pending
    /// <c>SupervisorDecision</c> wait. 2 min dwarfs the sub-second post-commit enqueue window, so a healthy
    /// turn that's mid-flight (its <c>ResumeWaitAsync</c> already enqueued) is never re-fired prematurely.
    /// </summary>
    public static readonly TimeSpan SupervisorAdvanceLostAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Threshold for "timer wake lost" — a Timer wait still Pending past its <c>wake_at</c> by this grace, on a
    /// Suspended run. Unlike <see cref="SupervisorDecision"/>, a dropped Timer wake (a lost Hangfire job scheduled at
    /// wake_at) has had NO backstop: the Stranded sweep excludes it (it HAS a pending wait) and nothing else re-fires
    /// it, so a crash between the Suspended commit and the schedule — or a purged Hangfire job — strands the run
    /// forever. 2 min dwarfs normal Hangfire fire latency (seconds after wake_at), so a healthy timer that just came
    /// due is never re-fired prematurely; the resume's own wait CAS makes a re-fire racing the real (late) job a no-op.
    /// </summary>
    public static readonly TimeSpan TimerWakeLostAfter = TimeSpan.FromMinutes(2);

    /// <summary>Batch size per sweep — bounds the work the reconciler can do in one tick so a backlog doesn't run forever.</summary>
    public const int BatchSize = 50;

    /// <summary>
    /// THE LOOP-GUARD (PR-E P1-2). The hard cap on how many times the reconciler re-dispatches a single
    /// abandoned-Running supervisor run with a recoverable in-flight decision. A mid-decision crash does NOT
    /// advance the supervisor's TurnNumber, and a re-dispatch RESETS <see cref="WorkflowRun.StartedAt"/> (the
    /// engine's Enqueued→Running CAS stamps it), so an UNBOUNDED recovery would re-dispatch a DETERMINISTICALLY
    /// crashing run every ~30 min forever — the run never terminates, never fails, and the supervisor's own
    /// MaxRounds/no-progress bounds can't catch it because the turn never settles. So we count the durable
    /// <c>supervisor.run_recovered</c> ledger records per run and STOP recovering at this cap; a run at/over it is
    /// left Running for <see cref="MarkAbandonedRunningAsync"/> to fail cleanly. Counted from the ledger (never an
    /// in-memory tally), so the bound survives a restart + can't be reset by re-dispatching. Small by design — a
    /// transient pod crash recovers in 1; a deterministic crash burns the budget then terminates.
    /// </summary>
    public const int MaxSupervisorRunRecoveries = 3;

    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowRunDispatcher _dispatcher;
    private readonly IWorkflowResumeService _resumeService;
    private readonly IRunRecordLogger _recordLogger;
    private readonly ILogger<StuckRunReconcilerService> _logger;

    public StuckRunReconcilerService(CodeSpaceDbContext db, IWorkflowRunDispatcher dispatcher, IWorkflowResumeService resumeService, IRunRecordLogger recordLogger, ILogger<StuckRunReconcilerService> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _resumeService = resumeService;
        _recordLogger = recordLogger;
        _logger = logger;
    }

    public async Task<StuckRunReconcileSummary> ReconcileAsync(CancellationToken cancellationToken)
    {
        var redispatched = await RedispatchStuckPendingAsync(cancellationToken).ConfigureAwait(false);
        var reverted = await RevertStuckEnqueuedAsync(cancellationToken).ConfigureAwait(false);

        // BEFORE the abandoned-Running failure sweep: a recovered supervisor run is flipped Running→Pending here,
        // so it no longer matches MarkAbandonedRunningAsync's Status==Running query in this same pass. A run at/over
        // the recovery cap is NOT recovered → it falls through to MarkAbandonedRunningAsync → Failure (clean termination).
        var recoveredSupervisorRuns = await RecoverAbandonedSupervisorRunsAsync(cancellationToken).ConfigureAwait(false);

        var abandoned = await MarkAbandonedRunningAsync(cancellationToken).ConfigureAwait(false);
        var unstranded = await RedispatchStrandedSuspendedAsync(cancellationToken).ConfigureAwait(false);

        // AFTER MarkAbandonedRunningAsync: a crashed rerun fork is flipped Running→Failure above, so this same pass
        // then frees its lease (the terminal-join catches it). Keeps the active-rerun lease's release complete even
        // when the engine's inline release was skipped (cancel paths) or failed.
        var releasedLeases = await ReleaseTerminatedRerunLeasesAsync(cancellationToken).ConfigureAwait(false);

        var supervisorAdvances = await RecoverSupervisorAdvancesAsync(cancellationToken).ConfigureAwait(false);

        var recoveredTimerWaits = await RecoverStrandedTimerWaitsAsync(cancellationToken).ConfigureAwait(false);

        var summary = new StuckRunReconcileSummary
        {
            RedispatchedFromPending = redispatched,
            RevertedFromEnqueued = reverted,
            MarkedAbandonedFromRunning = abandoned,
            RedispatchedFromStrandedSuspended = unstranded,
            RecoveredSupervisorAdvance = supervisorAdvances,
            RecoveredAbandonedSupervisorRun = recoveredSupervisorRuns,
            ReleasedRerunLeases = releasedLeases,
            RecoveredStrandedTimerWait = recoveredTimerWaits,
        };

        if (summary.Total > 0)
            _logger.LogInformation(
                "StuckRunReconciler: redispatched={Redispatched}, reverted={Reverted}, abandoned={Abandoned}, unstranded={Unstranded}, supervisorAdvances={SupervisorAdvances}, supervisorRunsRecovered={SupervisorRunsRecovered}, rerunLeasesReleased={RerunLeasesReleased}, timerWaitsRecovered={TimerWaitsRecovered}",
                summary.RedispatchedFromPending, summary.RevertedFromEnqueued, summary.MarkedAbandonedFromRunning, summary.RedispatchedFromStrandedSuspended, summary.RecoveredSupervisorAdvance, summary.RecoveredAbandonedSupervisorRun, summary.ReleasedRerunLeases, summary.RecoveredStrandedTimerWait);

        return summary;
    }

    /// <summary>
    /// Pending older than threshold: call DispatchAsync. The dispatcher's own CAS prevents
    /// double-dispatch if a normal flow is racing us; we just hand the id back into the
    /// queue and Hangfire takes it from there.
    /// </summary>
    private async Task<int> RedispatchStuckPendingAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - PendingStuckAfter;

        var stuckIds = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Status == WorkflowRunStatus.Pending && r.CreatedDate < threshold)
            .OrderBy(r => r.CreatedDate)
            .Take(BatchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var redispatched = 0;
        foreach (var runId in stuckIds)
        {
            try
            {
                if (await _dispatcher.DispatchAsync(runId, cancellationToken).ConfigureAwait(false)) redispatched++;
            }
            catch (Exception ex)
            {
                // Per-row resilience: one stuck row's failure doesn't abort the sweep.
                _logger.LogWarning(ex, "StuckRunReconciler: re-dispatch failed for run {RunId}; will retry next tick", runId);
            }
        }

        return redispatched;
    }

    /// <summary>
    /// Enqueued older than threshold: walk back to Pending via CAS. The CAS WHERE clause
    /// ensures we don't trample a worker that's just flipping to Running right now.
    /// <para>Staleness is judged against <see cref="WorkflowRun.EnqueuedAt"/> (set when the
    /// dispatcher's CAS Pending→Enqueued succeeds) rather than <c>LastModifiedDate</c>:
    /// <c>ExecuteUpdateAsync</c> bypasses EF's audit hook, so a run that sat in Pending for
    /// 11 minutes then transitioned to Enqueued would otherwise look IMMEDIATELY stuck
    /// because its audit timestamp still reflects the Pending creation time.</para>
    /// </summary>
    private async Task<int> RevertStuckEnqueuedAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - EnqueuedStuckAfter;

        return await _db.WorkflowRun
            .Where(r => r.Status == WorkflowRunStatus.Enqueued
                        && r.EnqueuedAt != null
                        && r.EnqueuedAt < threshold)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, WorkflowRunStatus.Pending)
                .SetProperty(r => r.EnqueuedAt, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Running older than threshold AND no recent ledger activity: mark Failure. We check
    /// the ledger to distinguish "worker crashed" (no activity) from "long-running but alive"
    /// (recent activity from a slow LLM call etc.). Marking Failure inline is safe HERE
    /// (unlike the engine entry, where it'd corrupt mid-execution) because the duration
    /// heuristic gives us high confidence the worker is gone.
    /// </summary>
    private async Task<int> MarkAbandonedRunningAsync(CancellationToken cancellationToken)
    {
        var runningThreshold = DateTimeOffset.UtcNow - RunningStuckAfter;
        var livenessThreshold = DateTimeOffset.UtcNow - LedgerLivenessWindow;

        // Find Running runs whose latest ledger record is older than the liveness window
        // (or has no ledger at all). The outer join via a sub-select keeps this to a single
        // round-trip; the resulting IDs are the candidates we mark.
        var candidates = await (
            from run in _db.WorkflowRun.AsNoTracking()
            where run.Status == WorkflowRunStatus.Running && run.StartedAt < runningThreshold
            let mostRecentLedger = _db.WorkflowRunRecord.AsNoTracking()
                .Where(rec => rec.RunId == run.Id)
                .Max(rec => (DateTimeOffset?)rec.OccurredAt)
            where mostRecentLedger == null || mostRecentLedger < livenessThreshold
            orderby run.StartedAt
            select run.Id
        ).Take(BatchSize).ToListAsync(cancellationToken).ConfigureAwait(false);

        var marked = 0;
        foreach (var runId in candidates)
        {
            // Atomic transition Running → Failure. Pinned to status=Running so a Cancel that
            // raced us doesn't get overwritten (Cancelled is also terminal; respect it).
            var now = DateTimeOffset.UtcNow;
            const string AbandonedError = "Run marked abandoned by reconciler — worker crashed or hung past " +
                                          "the abandoned-run threshold with no ledger progress. Replay the run to retry; " +
                                          "side-effecting nodes from the original run will NOT be re-fired.";

            var transitioned = await _db.WorkflowRun
                .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, WorkflowRunStatus.Failure)
                    .SetProperty(r => r.Error, AbandonedError)
                    .SetProperty(r => r.CompletedAt, (DateTimeOffset?)now), cancellationToken)
                .ConfigureAwait(false);

            if (transitioned == 0) continue;

            marked++;

            // Emit a run.failed ledger record so the run-detail UI surfaces the abandoned-by-
            // reconciler decision in the timeline. Failure to write the record doesn't undo
            // the status transition — we logged the warning + the operator can grep the run.
            try
            {
                var startedAt = await _db.WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.StartedAt).SingleAsync(cancellationToken).ConfigureAwait(false);
                var duration = startedAt.HasValue ? now - startedAt.Value : TimeSpan.Zero;
                await _recordLogger.RunFailedAsync(runId, AbandonedError, duration, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StuckRunReconciler: failed to emit run.failed ledger for abandoned run {RunId}", runId);
            }
        }

        return marked;
    }

    /// <summary>
    /// Suspended older than threshold AND with ZERO pending waits: CAS Suspended → Pending then
    /// re-dispatch. This is the safety net for the resume-flip-before-resolve race — the IMMEDIATE
    /// resume path (approval/action/callback/deadline/timer) is intentionally barrier-free, so a
    /// resume that resolves its wait in the tiny window AFTER an in-flight re-walk has already passed
    /// that branch node leaves the run Suspended with every wait Resolved and no dispatch coming
    /// (the resolver's flip no-op'd because the run was momentarily Running). The run is stranded.
    /// <para>The zero-pending-waits predicate is what makes this surgical: a legitimately-parked run
    /// (human approval awaited for hours, a timer/delay, a freshly-suspended map with K branch waits)
    /// always HAS a Pending wait → excluded. The threshold then excludes the sub-second resolve-then-
    /// flip window of a NORMAL last-wait resume (run momentarily Suspended with zero pending between
    /// the resolve CAS and the flip). So only a durably-stranded run survives both filters.</para>
    /// <para>We CAS Suspended → Pending FIRST (the dispatcher's CAS only matches Pending), then call
    /// DispatchAsync — exactly how the resume path flips then dispatches. The rowcount guard means a
    /// concurrent resume that already drove the run (its flip won) leaves us with 0 rows → we skip,
    /// no double-dispatch. The resolved waits rehydrate as the suspended nodes' ResumePayloads on the
    /// re-walk, so the run continues from where it stranded.</para>
    /// </summary>
    private async Task<int> RedispatchStrandedSuspendedAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - SuspendedStrandedAfter;

        var strandedIds = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Status == WorkflowRunStatus.Suspended
                        && r.LastModifiedDate < threshold
                        && !_db.WorkflowRunWait.Any(w => w.RunId == r.Id && w.Status == WorkflowWaitStatuses.Pending))
            .OrderBy(r => r.LastModifiedDate)
            .Take(BatchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var redispatched = 0;
        foreach (var runId in strandedIds)
        {
            try
            {
                // CAS Suspended → Pending. 0 rows = a concurrent resume already flipped + is driving
                // the dispatch (or the run advanced past Suspended). Skip — no double-dispatch.
                var flipped = await _db.WorkflowRun
                    .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Suspended)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Pending), cancellationToken)
                    .ConfigureAwait(false);

                if (flipped == 0) continue;

                // Now in Pending — hand back into the queue via the same path the resume uses. The
                // dispatcher's own Pending → Enqueued CAS is the final guard against a racing dispatch.
                if (await _dispatcher.DispatchAsync(runId, cancellationToken).ConfigureAwait(false)) redispatched++;
            }
            catch (Exception ex)
            {
                // Per-row resilience: one stranded row's failure doesn't abort the sweep.
                _logger.LogWarning(ex, "StuckRunReconciler: re-dispatch of stranded Suspended run {RunId} failed; will retry next tick", runId);
            }
        }

        return redispatched;
    }

    /// <summary>
    /// Free active-rerun leases whose fork reached a TERMINAL state — the complete backstop for the engine's inline
    /// release (which the cancel paths skip, and which is best-effort). A crashed fork is first flipped to Failure by
    /// <see cref="MarkAbandonedRunningAsync"/> earlier this pass, so its lease is caught here too; a hard-deleted fork
    /// frees its lease via the FK cascade. A legitimately Suspended fork (parked on a branch approval) KEEPS its lease.
    /// Set-based: one status-guarded UPDATE, returning the rows freed.
    /// </summary>
    private async Task<int> ReleaseTerminatedRerunLeasesAsync(CancellationToken cancellationToken)
    {
        return await _db.WorkflowRerunLease
            .Where(l => l.Status == RerunLeaseStatuses.InProgress
                        && _db.WorkflowRun.Any(r => r.Id == l.ForkRunId
                            && (r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, RerunLeaseStatuses.Released)
                .SetProperty(l => l.ReleasedAt, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// PR-E E2 — supervisor self-advance recovery. A run Suspended past the threshold with a pending
    /// <c>SupervisorDecision</c> wait will NEVER be woken externally (the wait has no external work item),
    /// and the Stranded sweep above excludes it (it HAS a pending wait). If the post-commit
    /// <see cref="WorkflowEngine.DispatchPendingSupervisorAdvanceAsync"/> enqueue was lost (a crash between
    /// the Suspended commit and the enqueue, or a dropped Hangfire job), only this sweep re-fires the
    /// self-advance — calling <see cref="IWorkflowResumeService.ResumeWaitAsync"/> exactly as the engine
    /// does, which resolves the wait + flips Suspended → Pending + re-dispatches. The resume's own
    /// wait-status CAS makes a re-fire racing a still-live original a no-op (idempotent).
    /// </summary>
    private async Task<int> RecoverSupervisorAdvancesAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - SupervisorAdvanceLostAfter;

        var stale = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.WaitKind == WorkflowWaitKinds.SupervisorDecision
                        && w.Status == WorkflowWaitStatuses.Pending
                        && w.CreatedAt < threshold
                        && _db.WorkflowRun.Any(r => r.Id == w.RunId && r.Status == WorkflowRunStatus.Suspended))
            .OrderBy(w => w.CreatedAt)
            .Take(BatchSize)
            .Select(w => new { w.RunId, w.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var recovered = 0;
        foreach (var wait in stale)
        {
            try
            {
                // Re-fire the SAME self-advance the engine enqueued. ResumeWaitAsync's wait CAS no-ops a
                // wait already resolved by a still-live original, so a re-fire is safe.
                if (await _resumeService.ResumeWaitAsync(wait.RunId, wait.Id, null, cancellationToken).ConfigureAwait(false)) recovered++;
            }
            catch (Exception ex)
            {
                // Per-row resilience: one stuck supervisor wait's failure doesn't abort the sweep.
                _logger.LogWarning(ex, "StuckRunReconciler: supervisor self-advance recovery for run {RunId} wait {WaitId} failed; will retry next tick", wait.RunId, wait.Id);
            }
        }

        return recovered;
    }

    /// <summary>
    /// Stranded-Timer recovery — the automated twin of the operator reissue verb. A Timer wait still Pending past its
    /// <c>wake_at</c> by <see cref="TimerWakeLostAfter"/>, on a Suspended run, means the scheduled Hangfire wake was
    /// lost (a crash between the Suspended commit and the schedule, or a purged job). Unlike every other backstopped
    /// wait it had no safety net (the Stranded sweep excludes it — it HAS a pending wait; and there's no equivalent of
    /// the <see cref="SupervisorDecision"/> self-advance sweep for it), so it would strand forever. Re-fire the wake
    /// exactly as the engine's scheduled job would — <see cref="IWorkflowResumeService.ResumeWaitAsync"/> with a null
    /// payload stamps the wake marker + resolves + re-dispatches — and the resume's own wait CAS makes a re-fire racing
    /// the real (late) job an idempotent no-op. Bounded by <see cref="BatchSize"/> per tick.
    /// </summary>
    private async Task<int> RecoverStrandedTimerWaitsAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - TimerWakeLostAfter;

        var stale = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.WaitKind == WorkflowWaitKinds.Timer
                        && w.Status == WorkflowWaitStatuses.Pending
                        && w.WakeAt != null
                        && w.WakeAt < threshold
                        && _db.WorkflowRun.Any(r => r.Id == w.RunId && r.Status == WorkflowRunStatus.Suspended))
            .OrderBy(w => w.WakeAt)
            .Take(BatchSize)
            .Select(w => new { w.RunId, w.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var recovered = 0;
        foreach (var wait in stale)
        {
            try
            {
                // Re-fire the timer wake exactly as the engine's scheduled job would. ResumeWaitAsync's wait CAS no-ops
                // a wake already resolved by a still-live (late) original, so a re-fire is safe.
                if (await _resumeService.ResumeWaitAsync(wait.RunId, wait.Id, null, cancellationToken).ConfigureAwait(false)) recovered++;
            }
            catch (Exception ex)
            {
                // Per-row resilience: one stranded timer's failure doesn't abort the sweep.
                _logger.LogWarning(ex, "StuckRunReconciler: stranded-timer recovery for run {RunId} wait {WaitId} failed; will retry next tick", wait.RunId, wait.Id);
            }
        }

        return recovered;
    }

    /// <summary>
    /// PR-E P1-2 — abandoned-Running supervisor recovery (THE worker-death crash-recovery loop). A worker that
    /// died MID-supervisor-decision left the run Running with a NON-terminal <c>SupervisorDecisionRecord</c> (the
    /// turn crashed after claiming the decision but before recording terminal — no exception reached the engine's
    /// catch, so the run was never failed). PR-1's frozen-replay can finish that in-flight decision
    /// deterministically on a re-walk, but nothing re-dispatches the Running-stuck run — so without this sweep it
    /// falls to <see cref="MarkAbandonedRunningAsync"/> and is FAILED instead of recovered. This sweep runs FIRST
    /// (the flip Running→Pending takes it out of that sweep's Status==Running query in the same pass) and CAS-flips
    /// each recoverable candidate Running→Pending then re-dispatches; the engine re-walk rehydrates the in-flight
    /// decision into <c>context.InFlight</c> and replays it exactly-once.
    /// <para>★ BOUNDED (the loop-guard): a mid-decision crash does NOT advance TurnNumber and a re-dispatch RESETS
    /// StartedAt, so an UNBOUNDED recovery would re-dispatch a DETERMINISTICALLY-crashing run forever. The candidate
    /// query EXCLUDES any run that already has <see cref="MaxSupervisorRunRecoveries"/> durable
    /// <c>supervisor.run_recovered</c> records — that run is left Running for <see cref="MarkAbandonedRunningAsync"/>
    /// to fail cleanly. So a transient crash recovers; a deterministic crash recovers K times then TERMINATES.</para>
    /// <para>The reaper always runs; a run with no non-terminal <c>SupervisorDecisionRecord</c> simply matches nothing here (the candidate query returns 0 for it) and falls through to <see cref="MarkAbandonedRunningAsync"/> unchanged.</para>
    /// </summary>
    private async Task<int> RecoverAbandonedSupervisorRunsAsync(CancellationToken cancellationToken)
    {
        var candidates = await FindRecoverableSupervisorRunsAsync(cancellationToken).ConfigureAwait(false);

        var recovered = 0;
        foreach (var runId in candidates)
            if (await TryRecoverSupervisorRunAsync(runId, cancellationToken).ConfigureAwait(false)) recovered++;

        return recovered;
    }

    /// <summary>
    /// The recoverable set: a Running run, started past the threshold, with a stale ledger (same liveness as
    /// <see cref="MarkAbandonedRunningAsync"/>), that HAS a non-terminal <c>SupervisorDecisionRecord</c> and is
    /// UNDER the recovery cap. Non-terminal is classified by the state machine (built once into
    /// <see cref="NonTerminalDecisionStatuses"/>) so the predicate can't drift from <c>IsTerminal</c>; the cap is
    /// the count of durable <c>supervisor.run_recovered</c> records — a ledger fact, so the bound survives restart.
    /// </summary>
    private async Task<List<Guid>> FindRecoverableSupervisorRunsAsync(CancellationToken cancellationToken)
    {
        var runningThreshold = DateTimeOffset.UtcNow - RunningStuckAfter;
        var livenessThreshold = DateTimeOffset.UtcNow - LedgerLivenessWindow;

        return await (
            from run in _db.WorkflowRun.AsNoTracking()
            where run.Status == WorkflowRunStatus.Running && run.StartedAt < runningThreshold
            let mostRecentLedger = _db.WorkflowRunRecord.AsNoTracking().Where(rec => rec.RunId == run.Id).Max(rec => (DateTimeOffset?)rec.OccurredAt)
            where mostRecentLedger == null || mostRecentLedger < livenessThreshold
            where _db.SupervisorDecisionRecord.Any(d => d.SupervisorRunId == run.Id && NonTerminalDecisionStatuses.Contains(d.Status))
            where _db.WorkflowRunRecord.Count(rec => rec.RunId == run.Id && rec.RecordType == WorkflowRunRecordTypes.SupervisorRunRecovered) < MaxSupervisorRunRecoveries
            orderby run.StartedAt
            select run.Id
        ).Take(BatchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recover one candidate: append the durable recovery marker FIRST (the bound counter — counted before the next
    /// pass even if the dispatch then fails), CAS Running→Pending (0 rows = a racing live worker re-claimed it →
    /// skip, never touch), then re-dispatch via the same path the resume uses. The dispatcher's own Pending→Enqueued
    /// CAS is the final guard against a racing dispatch; the engine re-walk replays the in-flight decision.
    /// </summary>
    private async Task<bool> TryRecoverSupervisorRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            var attempt = await CountPriorRecoveriesAsync(runId, cancellationToken).ConfigureAwait(false) + 1;
            await _recordLogger.SupervisorRunRecoveredAsync(runId, attempt, cancellationToken).ConfigureAwait(false);

            var flipped = await _db.WorkflowRun
                .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Pending), cancellationToken)
                .ConfigureAwait(false);

            if (flipped == 0) return false;

            return await _dispatcher.DispatchAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Per-row resilience: one run's recovery failure doesn't abort the sweep.
            _logger.LogWarning(ex, "StuckRunReconciler: abandoned-supervisor-run recovery for run {RunId} failed; will retry next tick", runId);
            return false;
        }
    }

    /// <summary>Count this run's durable <c>supervisor.run_recovered</c> records — the per-run recovery attempts so far (the loop-guard counter).</summary>
    private async Task<int> CountPriorRecoveriesAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunRecord.AsNoTracking()
            .CountAsync(rec => rec.RunId == runId && rec.RecordType == WorkflowRunRecordTypes.SupervisorRunRecovered, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The non-terminal <c>SupervisorDecisionStatus</c> set, derived ONCE from the state machine so the recoverable predicate can't drift from <see cref="SupervisorDecisionStateMachine.IsTerminal"/>.
    /// Includes <c>AwaitingApproval</c> (reserved/unused today). When the HITL-approval slice lands, such a decision parks the run <c>Suspended</c>, not <c>Running</c>, so this Running-only sweep still won't yank a legitimately-awaiting-approval run — revisit this predicate if that ever changes.</summary>
    private static readonly SupervisorDecisionStatus[] NonTerminalDecisionStatuses =
        Enum.GetValues<SupervisorDecisionStatus>().Where(s => !SupervisorDecisionStateMachine.IsTerminal(s)).ToArray();
}
