using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox;
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

    /// <summary>Operator-facing note stamped on a run recovered from its durable spool (it had finished while unobserved).</summary>
    public const string RecoveredError =
        "Recovered by the reconciler from the run's durable spool after its live observer went away (a worker " +
        "crash or backend restart) — the agent had already finished, so its outcome was salvaged rather than lost.";

    /// <summary>Operator-facing breadcrumb appended (best-effort) each time the reconciler re-attaches a stale-but-alive run, so the live timeline shows the gap. Informational only — the ceiling is gated on the hard <c>reattach_attempts</c> column (incremented in the reclaim's own transaction), so a failed breadcrumb can't stall it.</summary>
    public const string ReattachNote =
        "Re-attaching to this run after its worker stopped (a backend restart) — its detached process is still " +
        "alive, so the live timeline resumes from here.";

    /// <summary>Cap on reconciler re-attach attempts for one run: past it, a still-alive-but-unattachable run is abandoned rather than reclaimed forever (the no-livelock guarantee).</summary>
    public const int MaxReattachAttempts = 3;

    /// <summary>Operator-facing reason stamped on a still-Queued branch agent run the reconciler cancels because its parent workflow run reached a terminal state before the dispatch ran — so no sandbox/executor is launched for an already-dead workflow.</summary>
    public const string OrphanedParentTerminalError =
        "Agent run cancelled by the reconciler — its parent workflow run reached a terminal state (cancelled or " +
        "failed) before this branch was launched, so the staged run was never started.";

    /// <summary>Operator-facing reason stamped on a RUNNING branch agent run the reconciler cancels because its parent workflow run is terminal — the backstop for a live agent orphaned by the operator-cancel kill-wave's snapshot-vs-claim race. Its sandbox process is reaped so it stops holding its workspace + burning the injected model credential.</summary>
    public const string OrphanedParentTerminalRunningError =
        "Agent run cancelled by the reconciler — it was still running while its parent workflow run had reached a " +
        "terminal state (cancelled or failed), so the orphaned agent was stopped and its process reaped.";

    private readonly CodeSpaceDbContext _db;
    private readonly IAgentRunService _runs;
    private readonly IAgentRunCompletionNotifier _notifier;
    private readonly ICodeSpaceBackgroundJobClient _jobs;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IToolCallLedgerService _ledger;
    private readonly ILogger<AgentRunReconcilerService> _logger;

    public AgentRunReconcilerService(CodeSpaceDbContext db, IAgentRunService runs, IAgentRunCompletionNotifier notifier, ICodeSpaceBackgroundJobClient jobs, ISandboxRunnerRegistry runners, IToolCallLedgerService ledger, ILogger<AgentRunReconcilerService> logger)
    {
        _db = db;
        _runs = runs;
        _notifier = notifier;
        _jobs = jobs;
        _runners = runners;
        _ledger = ledger;
        _logger = logger;
    }

    public async Task<AgentRunReconcileSummary> ReconcileAsync(CancellationToken cancellationToken)
    {
        var orphansCancelled = await SweepRunningUnderTerminalParentAsync(cancellationToken).ConfigureAwait(false);

        var (abandoned, recovered, reattached) = await SweepStaleRunningAsync(cancellationToken).ConfigureAwait(false);

        var (resumed, reDispatched) = await ReconcilePendingWaitsAsync(cancellationToken).ConfigureAwait(false);

        if (orphansCancelled > 0 || abandoned > 0 || recovered > 0 || reattached > 0 || resumed > 0 || reDispatched > 0)
            _logger.LogInformation("AgentRunReconciler: cancelled {Orphans} running orphan(s) under terminal parents, abandoned {Abandoned}, recovered {Recovered} from spool, re-attached {Reattached} alive run(s), resumed {Resumed} stalled parent(s), re-dispatched {ReDispatched} stuck queued run(s)", orphansCancelled, abandoned, recovered, reattached, resumed, reDispatched);

        return new AgentRunReconcileSummary { CancelledRunningUnderTerminalParent = orphansCancelled, MarkedAbandonedFromRunning = abandoned, RecoveredFromSpool = recovered, ReattachedStaleRunning = reattached, ResumedStalledParents = resumed, ReDispatchedQueued = reDispatched };
    }

    /// <summary>
    /// Parent-terminal backstop for the operator-cancel kill-wave: cancel any branch <see cref="AgentRunStatus.Running"/>
    /// run whose parent workflow run is itself TERMINAL (Cancelled / Failure / Success). Unlike <see cref="SweepStaleRunningAsync"/>
    /// this does NOT predicate on lease-expiry or event-quiet — a freshly-claimed, heartbeating, event-emitting agent
    /// orphaned under a just-cancelled parent (the kill-wave's snapshot-vs-claim race) is live, so the liveness sweep
    /// would never touch it. Killing it via <see cref="IAgentRunService.CancelRunningAsync"/> (Running-guarded, epoch-fenced
    /// CAS + a best-effort durable process kill) flips it Cancelled with its process reaped — the right terminal state
    /// for an operator cancel (not the abandon path's Failed). Idempotent + status-guarded, so a worker landing the run
    /// terminal in the same instant simply wins the CAS and is left alone. A per-item failure is logged + retried next
    /// sweep, never aborting the batch.
    /// </summary>
    private async Task<int> SweepRunningUnderTerminalParentAsync(CancellationToken cancellationToken)
    {
        var candidates = await _db.AgentRun.AsNoTracking()
            .Where(r => r.Status == AgentRunStatus.Running
                        && r.WorkflowRunId != null
                        && _db.WorkflowRun.Any(p => p.Id == r.WorkflowRunId
                            && (p.Status == WorkflowRunStatus.Cancelled || p.Status == WorkflowRunStatus.Failure || p.Status == WorkflowRunStatus.Success)))
            .OrderBy(r => r.CreatedDate)
            .Take(BatchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cancelled = 0;

        foreach (var runId in candidates)
            cancelled += await CancelOrphanRunningAsync(runId, cancellationToken).ConfigureAwait(false);

        return cancelled;
    }

    /// <summary>Cancel one Running branch agent orphaned under a terminal parent, via the Running-guarded CAS (+ durable kill). A failure is logged + retried next sweep — never throws out of the sweep.</summary>
    private async Task<int> CancelOrphanRunningAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _runs.CancelRunningAsync(runId, OrphanedParentTerminalRunningError, cancellationToken).ConfigureAwait(false))
                return 0;

            await TryAppendEventAsync(runId, AgentEventKind.Error, OrphanedParentTerminalRunningError, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("AgentRunReconciler: cancelled running agent run {RunId} whose parent workflow run is terminal (orphaned by the kill-wave race)", runId);
            return 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentRunReconciler: failed to cancel running orphan agent run {RunId}; will retry next sweep", runId);
            return 0;
        }
    }

    /// <summary>
    /// For each Running run whose LEASE has lapsed (the claiming worker stopped renewing it — it died/hung): if
    /// it carries a durable runner handle, PROBE it before abandoning — a run that finished while unobserved (its
    /// observer crashed mid-tail, or the backend restarted) is RECOVERED from its exit marker instead of being
    /// lost; one still alive is left for a future re-attach; one truly gone is abandoned. A run with no handle
    /// (non-durable runner) keeps the blind-abandon behaviour. The lapsed lease is ground-truth liveness (a live
    /// worker keeps it fresh); the no-recent-events second signal still shields a streaming run whose lease lapsed
    /// from a stray timing edge. Every transition is the same status-guarded CAS, so a worker landing the run
    /// right now wins.
    /// </summary>
    private async Task<(int Abandoned, int Recovered, int Reattached)> SweepStaleRunningAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var eventThreshold = now - AgentRunLiveness.Window;

        var candidates = await _db.AgentRun.AsNoTracking()
            .Where(r => r.Status == AgentRunStatus.Running
                        && (r.LeaseExpiresAt == null || r.LeaseExpiresAt < now)
                        && !_db.AgentRunEvent.Any(e => e.AgentRunId == r.Id && e.OccurredAt >= eventThreshold))
            .OrderBy(r => r.LeaseExpiresAt)
            .Take(BatchSize)
            .Select(r => new { r.Id, r.RunnerHandleJson, r.ReattachAttempts })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var abandoned = 0;
        var recovered = 0;
        var reattached = 0;

        foreach (var c in candidates)
            switch (await ResolveStaleRunAsync(c.Id, c.RunnerHandleJson, c.ReattachAttempts, cancellationToken).ConfigureAwait(false))
            {
                case StaleOutcome.Recovered: recovered++; break;
                case StaleOutcome.Abandoned: abandoned++; break;
                case StaleOutcome.Reattached: reattached++; break;
            }

        return (abandoned, recovered, reattached);
    }

    /// <summary>Decide one stale run's fate: probe its durable handle (recover / leave-alone / abandon), or blind-abandon when there's no usable handle or the probe fails.</summary>
    private async Task<StaleOutcome> ResolveStaleRunAsync(Guid runId, string? handleJson, int reattachAttempts, CancellationToken cancellationToken)
    {
        var durable = ResolveDurableRunner(handleJson, out var handle);

        if (durable is null || handle is null)
            return await AbandonAsync(runId, cancellationToken).ConfigureAwait(false);

        var probe = await ProbeQuietlyAsync(durable, handle, runId, cancellationToken).ConfigureAwait(false);

        if (probe is null)
            return await AbandonAsync(runId, cancellationToken, durable, handle).ConfigureAwait(false);   // can't probe → kill the maybe-alive orphan, then abandon (don't leave it stuck)

        if (probe.State == SandboxRunState.Exited)
            return await RecoverFromSpoolAsync(runId, probe.ExitCode ?? -1, cancellationToken).ConfigureAwait(false);

        if (probe.State == SandboxRunState.Gone)
            return await AbandonAsync(runId, cancellationToken).ConfigureAwait(false);   // process already gone — nothing to kill

        // Running: the supervised process is still ALIVE but its worker vanished. Past the re-attach ceiling, KILL
        // it and abandon — a permanently-unattachable-but-alive run must still reach a terminal state, and leaving
        // its process running would keep burning the injected model credential. Otherwise re-attach a fresh observer
        // to resume the live timeline + complete it.
        if (reattachAttempts >= MaxReattachAttempts)
        {
            _logger.LogWarning("AgentRunReconciler: agent run {RunId} is alive but exhausted {Max} re-attach attempts; killing the orphan and abandoning", runId, MaxReattachAttempts);
            return await AbandonAsync(runId, cancellationToken, durable, handle).ConfigureAwait(false);
        }

        return await ReattachAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-attach a stale-but-alive Running run: atomically reclaim it (bump the fence epoch + re-lease + INCREMENT
    /// the attempt counter, status-guarded) and dispatch <see cref="IAgentRunExecutor.ReattachAsync"/> to resume
    /// tailing + complete it. The fresh lease drops the run out of the candidate set until the re-attaching worker
    /// renews it, so it isn't re-dispatched every sweep; losing the reclaim CAS (another replica won / it already
    /// landed terminal) leaves it alone. The attempt ceiling is enforced by the caller (which has the durable
    /// handle to kill the orphan on the past-ceiling abandon).
    /// </summary>
    private async Task<StaleOutcome> ReattachAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!await _runs.ReclaimForReattachAsync(runId, cancellationToken).ConfigureAwait(false))
            return StaleOutcome.LeftAlone;   // lost the reclaim CAS — another replica won, or it just landed terminal

        await TryAppendEventAsync(runId, AgentEventKind.Warning, ReattachNote, cancellationToken).ConfigureAwait(false);
        _jobs.Enqueue<IAgentRunExecutor>(e => e.ReattachAsync(runId, CancellationToken.None));

        _logger.LogInformation("AgentRunReconciler: re-attaching agent run {RunId} (its durable process is alive but its worker vanished)", runId);
        return StaleOutcome.Reattached;
    }

    /// <summary>
    /// Atomic CAS Running → Failed (the abandon path), pinned to status=Running so a worker completing right now
    /// wins. When the run carries a live durable handle (<paramref name="durable"/> + <paramref name="handle"/>),
    /// TERMINATE its orphaned process tree AFTER a won CAS — a still-alive agent would otherwise run on to its
    /// wall-clock deadline, holding the workspace and burning the injected model credential after the DB says
    /// Failed. Killing only on a won CAS means a run another replica/worker just legitimately landed (lost CAS) is
    /// never killed out from under it. The kill is best-effort; the abandon stands regardless. Appends the
    /// abandoned-run event when it transitions.
    /// </summary>
    private async Task<StaleOutcome> AbandonAsync(Guid runId, CancellationToken cancellationToken, ISandboxDurableRunner? durable = null, SandboxHandle? handle = null)
    {
        var transitioned = await _db.AgentRun
            .Where(r => r.Id == runId && r.Status == AgentRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AgentRunStatus.Failed)
                .SetProperty(r => r.Error, AbandonedError)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (transitioned == 0) return StaleOutcome.LeftAlone;

        if (durable is not null && handle is not null)
            await TerminateQuietlyAsync(durable, handle, runId, cancellationToken).ConfigureAwait(false);

        await TryAppendEventAsync(runId, AgentEventKind.Error, AbandonedError, cancellationToken).ConfigureAwait(false);
        return StaleOutcome.Abandoned;
    }

    /// <summary>Kill the abandoned run's orphaned process tree via its durable handle, swallowing any failure (the run still reached Failed; at worst the process lingers to its deadline) so a kill error never aborts the sweep.</summary>
    private async Task TerminateQuietlyAsync(ISandboxDurableRunner durable, SandboxHandle handle, Guid runId, CancellationToken cancellationToken)
    {
        try { await durable.TerminateAsync(handle, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentRunReconciler: failed to terminate the orphaned process for abandoned run {RunId}; it may keep running until its wall-clock deadline", runId);
        }
    }

    /// <summary>
    /// Salvage a run whose durable spool shows it already finished: CAS Running → Succeeded/Failed by the exit
    /// code, with a result + an event noting the recovery. The raw spool output is NOT folded in here — the
    /// reconciler can't decrypt the run's secret to redact it, so it persists only the exit code; the redacted
    /// events the live observer streamed before it died are already in the log.
    /// </summary>
    private async Task<StaleOutcome> RecoverFromSpoolAsync(Guid runId, int exitCode, CancellationToken cancellationToken)
    {
        var result = new AgentRunResult { Status = exitCode == 0 ? AgentRunStatus.Succeeded : AgentRunStatus.Failed, ExitReason = "recovered-from-spool", Error = exitCode == 0 ? null : $"{RecoveredError} The agent exited with code {exitCode}." };

        // Completion contract (Slice A1): even on this crash-recovery path, a clean exit can't be called Succeeded while a
        // decision the run raised is still unanswered — re-grade to NeedsReview(NeedsDecision) so the invariant holds here
        // too, mirroring AgentRunService.CompleteCoreAsync. Only a would-be Succeeded needs the lookup.
        if (result.Status == AgentRunStatus.Succeeded)
        {
            var pendingDecisionId = await _ledger.FindBlockingDecisionIdAsync(runId, cancellationToken).ConfigureAwait(false);
            result = AgentCompletionContract.ApplyPendingDecision(result, pendingDecisionId);
        }

        var status = result.Status;
        var error = result.Error;
        var resultJson = JsonSerializer.Serialize(result, AgentJson.Options);

        var transitioned = await _db.AgentRun
            .Where(r => r.Id == runId && r.Status == AgentRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.ResultJson, resultJson)
                .SetProperty(r => r.Error, error)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (transitioned == 0) return StaleOutcome.LeftAlone;   // a worker (or another replica) landed it first

        var kind = status switch
        {
            AgentRunStatus.Succeeded => AgentEventKind.Completed,
            AgentRunStatus.NeedsReview => AgentEventKind.Warning,   // recovered clean, but a raised decision is still unanswered — needs a human
            _ => AgentEventKind.Error,
        };
        await TryAppendEventAsync(runId, kind, $"{RecoveredError} (exit {exitCode})", cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("AgentRunReconciler: recovered agent run {RunId} from its durable spool as {Status} (exit {Exit})", runId, status, exitCode);
        return StaleOutcome.Recovered;
    }

    /// <summary>Resolve the durable runner for a persisted handle, or null when the handle is absent/unparseable or its runner isn't durable (then the caller blind-abandons).</summary>
    private ISandboxDurableRunner? ResolveDurableRunner(string? handleJson, out SandboxHandle? handle)
    {
        handle = null;

        if (string.IsNullOrWhiteSpace(handleJson)) return null;

        SandboxHandle? parsed;
        try { parsed = JsonSerializer.Deserialize<SandboxHandle>(handleJson, AgentJson.Options); }
        catch (JsonException) { return null; }

        if (parsed is null) return null;

        handle = parsed;
        return _runners.All.FirstOrDefault(r => r.Kind == parsed.Kind) as ISandboxDurableRunner;
    }

    /// <summary>Probe the handle, swallowing any failure (a missing spool dir, an IO error) as null so the caller falls back to a clean abandon rather than throwing out of the sweep.</summary>
    private async Task<SandboxProbe?> ProbeQuietlyAsync(ISandboxDurableRunner durable, SandboxHandle handle, Guid runId, CancellationToken cancellationToken)
    {
        try { return await durable.ProbeAsync(handle, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentRunReconciler: failed to probe the durable handle for {RunId}; falling back to abandon", runId);
            return null;
        }
    }

    /// <summary>
    /// The backstop for the agent → workflow hand-off: for every workflow run still parked on a pending
    /// <c>AgentRun</c> wait, unstick the agent run it's waiting on. A run that's already terminal but whose
    /// parent never resumed (a crashed worker, a reconciler-abandoned run, or an executor whose best-effort
    /// notify failed) → fire the completion notifier so the parent resumes. A run stuck <c>Queued</c> whose
    /// PARENT workflow run is itself terminal (Cancelled/Failure — e.g. a map branch staged-but-undispatched
    /// under a run later cancelled) → CANCEL it, never launch a sandbox for an already-dead workflow. A run
    /// stuck <c>Queued</c> past the liveness window whose parent is still live (Suspended/Pending/Running) →
    /// re-dispatch the executor (the normal durable-recovery path; the claim guard makes a duplicate a no-op).
    /// Idempotent + retried every sweep (a resume flips the wait Resolved so it drops out next tick); a
    /// per-item failure is logged and retried, never aborting the sweep.
    /// </summary>
    private async Task<(int Resumed, int ReDispatched)> ReconcilePendingWaitsAsync(CancellationToken cancellationToken)
    {
        var waitingIds = await PendingAgentRunWaitIdsAsync(cancellationToken).ConfigureAwait(false);

        if (waitingIds.Count == 0) return (0, 0);

        var staleThreshold = DateTimeOffset.UtcNow - AgentRunLiveness.Window;

        var runs = await _db.AgentRun.AsNoTracking()
            .Where(r => waitingIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Status, r.CreatedDate, r.WorkflowRunId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var terminalParents = await TerminalParentRunIdsAsync(runs.Select(r => r.WorkflowRunId), cancellationToken).ConfigureAwait(false);

        var resumed = 0;
        foreach (var run in runs.Where(r => AgentRunStateMachine.IsTerminal(r.Status)))
            resumed += await TryResumeParentAsync(run.Id, run.Status, cancellationToken).ConfigureAwait(false);

        var reDispatched = 0;
        foreach (var run in runs.Where(r => r.Status == AgentRunStatus.Queued))
        {
            // A Queued branch run whose PARENT workflow run is already terminal must NEVER launch — cancel it
            // (no sandbox/executor for a dead workflow). A still-live parent (Suspended/Pending/Running) keeps
            // the normal stale-window re-dispatch — the durable-recovery path this guard must not break.
            if (run.WorkflowRunId is { } parentId && terminalParents.Contains(parentId))
                await CancelOrphanedQueuedAsync(run.Id, cancellationToken).ConfigureAwait(false);
            else if (run.CreatedDate < staleThreshold)
                reDispatched += TryReDispatch(run.Id, run.CreatedDate);
        }

        return (resumed, reDispatched);
    }

    /// <summary>The subset of the supplied parent workflow-run ids whose run is in a TERMINAL state (Cancelled / Failure / Success) — a Queued branch agent run parked under one of these must be cancelled, not launched. Nulls (standalone agent runs with no parent) are skipped.</summary>
    private async Task<HashSet<Guid>> TerminalParentRunIdsAsync(IEnumerable<Guid?> parentRunIds, CancellationToken cancellationToken)
    {
        var ids = parentRunIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

        if (ids.Count == 0) return new HashSet<Guid>();

        var terminal = await _db.WorkflowRun.AsNoTracking()
            .Where(r => ids.Contains(r.Id) && (r.Status == WorkflowRunStatus.Cancelled || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Success))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return terminal.ToHashSet();
    }

    /// <summary>Cancel a still-Queued branch agent run orphaned under a now-terminal parent workflow run, via the Queued-guarded CAS (a worker that just claimed it loses 0 rows and is left alone). A failure is logged + retried next sweep — never throws out of the sweep.</summary>
    private async Task CancelOrphanedQueuedAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            if (await _runs.CancelQueuedAsync(runId, OrphanedParentTerminalError, cancellationToken).ConfigureAwait(false))
            {
                await TryAppendEventAsync(runId, AgentEventKind.Error, OrphanedParentTerminalError, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("AgentRunReconciler: cancelled queued agent run {RunId} whose parent workflow run is terminal (never launched)", runId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentRunReconciler: failed to cancel orphaned queued agent run {RunId}; will retry next sweep", runId);
        }
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

    /// <summary>Append one reconciler-authored event (abandonment / recovery / re-attach note) so the live log / replay timeline shows what happened. Best-effort — a logging failure doesn't undo the transition.</summary>
    private async Task TryAppendEventAsync(Guid runId, AgentEventKind kind, string text, CancellationToken cancellationToken)
    {
        var record = new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = runId, Kind = kind, Text = text };

        try
        {
            _db.AgentRunEvent.Add(record);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // DETACH the failed insert so it doesn't stay tracked on this shared scoped DbContext and get
            // re-attempted (and re-fail) by every later candidate's SaveChanges in the same sweep batch.
            _db.Entry(record).State = EntityState.Detached;
            _logger.LogWarning(ex, "AgentRunReconciler: failed to append the {Kind} event for {RunId}", kind, runId);
        }
    }
}

/// <summary>What the sweep did with one stale-Running run.</summary>
internal enum StaleOutcome
{
    /// <summary>Left as-is — a worker landed it first, or its durable process is still alive (left for re-attach).</summary>
    LeftAlone,

    /// <summary>Flipped Running → Failed because its worker vanished and the run was not recoverable.</summary>
    Abandoned,

    /// <summary>Salvaged from its durable spool: the run had already finished while unobserved.</summary>
    Recovered,

    /// <summary>Still alive but its worker vanished: re-claimed (epoch bumped + re-leased) and a re-attach worker dispatched to resume + complete it.</summary>
    Reattached,
}

/// <summary>Diagnostic summary of one reconcile sweep. Returned for log surfacing + the recurring-job result.</summary>
public sealed record AgentRunReconcileSummary
{
    /// <summary>Running branch-agent runs cancelled because their parent workflow run was terminal — the kill-wave's parent-terminal backstop (closes the snapshot-vs-claim orphan window regardless of lease/event liveness).</summary>
    public int CancelledRunningUnderTerminalParent { get; init; }

    /// <summary>Running runs flipped to Failed because their worker vanished (stale heartbeat + no events) and they were not recoverable.</summary>
    public int MarkedAbandonedFromRunning { get; init; }

    /// <summary>Running runs salvaged from their durable spool — the agent had already finished while its live observer was gone.</summary>
    public int RecoveredFromSpool { get; init; }

    /// <summary>Still-alive Running runs whose worker vanished — re-claimed + a re-attach worker dispatched to resume the live timeline and complete them.</summary>
    public int ReattachedStaleRunning { get; init; }

    /// <summary>Workflow runs resumed off a terminal agent run that hadn't propagated its completion (crash / failed notify).</summary>
    public int ResumedStalledParents { get; init; }

    /// <summary>Stuck-Queued agent runs whose dispatch was lost and were re-enqueued to the executor.</summary>
    public int ReDispatchedQueued { get; init; }

    public int Total => CancelledRunningUnderTerminalParent + MarkedAbandonedFromRunning + RecoveredFromSpool + ReattachedStaleRunning + ResumedStalledParents + ReDispatchedQueued;
}
