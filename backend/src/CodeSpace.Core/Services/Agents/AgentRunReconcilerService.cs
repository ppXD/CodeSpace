using System.Text.Json;
using CodeSpace.Core.Constants;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Recovery;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Jobs;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Recovery;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
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
/// <para>Stale-run decisions retain their scanned owner, epoch and handle across asynchronous probes. A final
/// row lock rechecks that identity and database lease expiry before terminal writes or a reattach reservation.</para>
///
/// <para>On a MULTI-HOST deployment the sweep also has to respect what it cannot see. A durable handle's liveness is
/// a pid inside the launching host's process namespace, so a sweep landing on any other host gets
/// <see cref="SandboxRunState.Indeterminate"/> and DEFERS instead of abandoning — the fleet-wide minutely cron makes a
/// sweep on the owning host the thing that answers it. The no-stuck-run guarantee therefore has one honest
/// qualification: a run whose host never returns is terminalized at its own wall-clock deadline
/// (<see cref="DeferToTheMintingHostAsync"/>), and a run launched with no deadline at all waits for that host or an
/// operator.</para>
///
/// <para>That terminalization also has to account for what it CANNOT clean up. Every resource a run holds — its
/// spool, its filtered-egress netns and the host-global subnet lease behind it, its cgroup leaf, its workspace clone
/// — lives on the host that launched it, so an abandon performed anywhere else can free none of them. It therefore
/// writes a typed <see cref="Messages.Agents.Recovery.RunCleanupReceipt"/> per resource instead of running local
/// teardowns against foreign keys and swallowing the miss, and <c>AgentRunOrphanReaper</c> on the owning host is what
/// settles those claims.</para>
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

    /// <summary>
    /// Wall-clock the whole sweep may spend re-discovering admitted launches. Each adoption waits on a launch receipt
    /// for the runner's own bounded patience, so <see cref="BatchSize"/> handle-less candidates in one host-crash
    /// stampede would otherwise outrun the sweep's own cadence. Past this budget the remaining candidates that HAVE a
    /// live recorded attempt are left alone for the next sweep rather than adopted or abandoned — deferring a decision
    /// costs a minute, and abandoning one costs a live agent's clone.
    /// </summary>
    public static readonly TimeSpan AdoptionSweepBudget = TimeSpan.FromSeconds(10);

    /// <summary>Recorded on a cleanup receipt when a teardown was attempted on the owning host and threw — the row that replaces a silent best-effort warning.</summary>
    public const string TeardownFailedCode = "teardown-failed";

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

    /// <summary>Operator-facing reason stamped on a still-Queued branch agent run the reconciler cancels because it has NO parent workflow run and NO wait referencing it — a parentless orphan no other sweep or reclaim path can collect, which would otherwise sit Queued forever against the admission cap.</summary>
    public const string OrphanedNoParentQueuedError =
        "Agent run cancelled by the reconciler — it sat queued past the liveness window with no parent workflow run " +
        "and nothing waiting on it, so it could never be launched, reclaimed, or consumed. Re-run the agent to retry.";

    private readonly CodeSpaceDbContext _db;
    private readonly IAgentRunService _runs;
    private readonly IAgentRunCompletionNotifier _notifier;
    private readonly ICodeSpaceBackgroundJobClient _jobs;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IToolCallLedgerService _ledger;
    private readonly Capture.ICaptureIntentService _captureIntents;
    private readonly Capture.INativeRecordPlane _nativeRecords;
    private readonly IRunCleanupLedger _cleanup;
    private readonly AgentRunLogging.IAgentRunLogService _logs;
    // Withdraws an abandoned run's brokered model credential before its orphaned process is killed. Optional so a
    // deployment (or a hand-built double) without a broker has nothing to withdraw.
    private readonly Credentials.IModelCredentialBroker? _credentialBroker;
    private readonly ILogger<AgentRunReconcilerService> _logger;

    public AgentRunReconcilerService(CodeSpaceDbContext db, IAgentRunService runs, IAgentRunCompletionNotifier notifier, ICodeSpaceBackgroundJobClient jobs, ISandboxRunnerRegistry runners, IToolCallLedgerService ledger, Capture.ICaptureIntentService captureIntents, Capture.INativeRecordPlane nativeRecords, IRunCleanupLedger cleanup, AgentRunLogging.IAgentRunLogService logs, ILogger<AgentRunReconcilerService> logger, Credentials.IModelCredentialBroker? credentialBroker = null)
    {
        _db = db;
        _runs = runs;
        _notifier = notifier;
        _jobs = jobs;
        _runners = runners;
        _ledger = ledger;
        _captureIntents = captureIntents;
        _nativeRecords = nativeRecords;
        _cleanup = cleanup;
        _logs = logs;
        _credentialBroker = credentialBroker;
        _logger = logger;
    }

    public async Task<AgentRunReconcileSummary> ReconcileAsync(CancellationToken cancellationToken)
    {
        var orphansCancelled = await SweepRunningUnderTerminalParentAsync(cancellationToken).ConfigureAwait(false);

        var queuedOrphansCancelled = await SweepQueuedUnderTerminalParentAsync(cancellationToken).ConfigureAwait(false);

        var parentlessOrphansCancelled = await SweepOrphanedParentlessQueuedAsync(cancellationToken).ConfigureAwait(false);

        var (abandoned, recovered, reattached) = await SweepStaleRunningAsync(cancellationToken).ConfigureAwait(false);

        // P2 (capture-intent saga): dangling promises of already-terminal runs — any ordering the per-run
        // recovery marks below missed — become INDETERMINATE here, the same safety-net shape as the tool-call reaper.
        await _captureIntents.SweepDanglingForTerminalRunsAsync(batchSize: 50, cancellationToken).ConfigureAwait(false);

        var (resumed, reDispatched) = await ReconcilePendingWaitsAsync(cancellationToken).ConfigureAwait(false);

        if (orphansCancelled > 0 || queuedOrphansCancelled > 0 || parentlessOrphansCancelled > 0 || abandoned > 0 || recovered > 0 || reattached > 0 || resumed > 0 || reDispatched > 0)
            _logger.LogInformation("AgentRunReconciler: cancelled {Orphans} running orphan(s) under terminal parents, cancelled {QueuedOrphans} queued orphan(s) under terminal parents, cancelled {ParentlessOrphans} parentless queued orphan(s), abandoned {Abandoned}, recovered {Recovered} from spool, re-attached {Reattached} alive run(s), resumed {Resumed} stalled parent(s), re-dispatched {ReDispatched} stuck queued run(s)", orphansCancelled, queuedOrphansCancelled, parentlessOrphansCancelled, abandoned, recovered, reattached, resumed, reDispatched);

        return new AgentRunReconcileSummary { CancelledRunningUnderTerminalParent = orphansCancelled, CancelledQueuedUnderTerminalParent = queuedOrphansCancelled, CancelledParentlessQueued = parentlessOrphansCancelled, MarkedAbandonedFromRunning = abandoned, RecoveredFromSpool = recovered, ReattachedStaleRunning = reattached, ResumedStalledParents = resumed, ReDispatchedQueued = reDispatched };
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
            if (!await _runs.CancelRunningAsync(runId, OrphanedParentTerminalRunningError, AgentRunAbandonCause.ParentTerminal, cancellationToken).ConfigureAwait(false))
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
    /// The Queued-orphan backstop — the symmetric partner of <see cref="SweepRunningUnderTerminalParentAsync"/>: cancel
    /// any branch <see cref="AgentRunStatus.Queued"/> run whose parent workflow run is TERMINAL and which NO AgentRun
    /// wait references. That is the one uncollectable leak <see cref="ReconcilePendingWaitsAsync"/> can't see (it only
    /// inspects wait-referenced runs) and <see cref="SweepStaleRunningAsync"/> can't see (it's Running-only): an
    /// <c>agent.run</c> / supervisor suspension commits the Queued run (CreateAsync) but crashes BEFORE its
    /// <c>workflow_run_wait</c> commits, so the row has no wait — the staged executor never launches, and the run sits
    /// Queued forever, permanently counted against the <see cref="AdmissionController"/> in-flight cap. A still-Queued
    /// run under a LIVE parent is deliberately left alone: it may be a healthy just-staged run, or a supervisor
    /// crash-orphan the next spawn pass reclaims (<c>RealSupervisorActionExecutor.ReclaimableOrphanAgentIdsAsync</c>) —
    /// only a TERMINAL parent proves it will never run or be reused. Queued-guarded CAS, so a worker claiming the run in
    /// the same instant wins; a per-item failure is logged + retried next sweep, never aborting the batch.
    /// </summary>
    private async Task<int> SweepQueuedUnderTerminalParentAsync(CancellationToken cancellationToken)
    {
        var candidates = await _db.AgentRun.AsNoTracking()
            .Where(r => r.Status == AgentRunStatus.Queued
                        && r.WorkflowRunId != null
                        && _db.WorkflowRun.Any(p => p.Id == r.WorkflowRunId
                            && (p.Status == WorkflowRunStatus.Cancelled || p.Status == WorkflowRunStatus.Failure || p.Status == WorkflowRunStatus.Success)))
            .OrderBy(r => r.CreatedDate)
            .Take(BatchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0) return 0;

        var orphanIds = await OrphanIdsWithoutWaitAsync(candidates, cancellationToken).ConfigureAwait(false);

        var cancelled = 0;

        foreach (var runId in orphanIds)
            cancelled += await CancelOrphanedQueuedAsync(runId, OrphanedParentTerminalError, cancellationToken).ConfigureAwait(false);

        return cancelled;
    }

    /// <summary>
    /// The PARENTLESS-orphan backstop — the third leg of the Queued-orphan collection (partner to
    /// <see cref="SweepQueuedUnderTerminalParentAsync"/>, which owns the terminal-parent leg, and
    /// <see cref="ReconcilePendingWaitsAsync"/>, which owns the wait-referenced redispatch leg). Cancel any branch
    /// <see cref="AgentRunStatus.Queued"/> run that has NO parent workflow run (<c>WorkflowRunId == null</c>), NO wait
    /// referencing it, and has sat Queued past the liveness window. Such a run is uncollectable by every other path —
    /// the terminal-parent sweeps skip it (they require a parent), the wait-referenced redispatch skips it (no wait),
    /// and there is no supervisor/workflow to reclaim or resume it — so without this it would sit Queued forever,
    /// permanently counted against the <see cref="AdmissionController"/> in-flight cap (the same leak the terminal-parent
    /// Queued sweep guards, for the no-parent case). It never ran (a Queued run has no side effects) and nothing consumes
    /// its result, so cancelling frees the slot + stamps a legible terminal state. The stale-window gate leaves a
    /// just-created parentless run (e.g. a benchmark run about to claim inline) untouched; the Queued-guarded CAS lets a
    /// worker claiming the run in the same instant win. A per-item failure is logged + retried next sweep.
    /// </summary>
    private async Task<int> SweepOrphanedParentlessQueuedAsync(CancellationToken cancellationToken)
    {
        var staleThreshold = DateTimeOffset.UtcNow - AgentRunLiveness.Window;

        var candidates = await _db.AgentRun.AsNoTracking()
            .Where(r => r.Status == AgentRunStatus.Queued && r.WorkflowRunId == null && r.CreatedDate < staleThreshold)
            .OrderBy(r => r.CreatedDate)
            .Take(BatchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0) return 0;

        var orphanIds = await OrphanIdsWithoutWaitAsync(candidates, cancellationToken).ConfigureAwait(false);

        var cancelled = 0;

        foreach (var runId in orphanIds)
            cancelled += await CancelOrphanedQueuedAsync(runId, OrphanedNoParentQueuedError, cancellationToken).ConfigureAwait(false);

        return cancelled;
    }

    /// <summary>Of the candidate Queued run ids, those NO AgentRun wait references — the genuine split orphans (the wait commit was lost). A run WITH a wait is the wait-referenced case <see cref="ReconcilePendingWaitsAsync"/> already owns, so it's excluded here to avoid a double-collect. (A Queued run can't carry a Resolved wait — it never ran — so this is the complete "no wait at all" set.)</summary>
    private async Task<List<Guid>> OrphanIdsWithoutWaitAsync(IReadOnlyList<Guid> candidateIds, CancellationToken cancellationToken)
    {
        var tokens = candidateIds.Select(id => id.ToString()).ToList();

        var referenced = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.WaitKind == WorkflowWaitKinds.AgentRun && tokens.Contains(w.Token))
            .Select(w => w.Token)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var referencedSet = referenced.ToHashSet();

        return candidateIds.Where(id => !referencedSet.Contains(id.ToString())).ToList();
    }

    /// <summary>
    /// For each Running run whose LEASE has lapsed (the claiming worker stopped renewing it — it died/hung): if
    /// it carries a durable runner handle, PROBE it before abandoning — a run that finished while unobserved (its
    /// observer crashed mid-tail, or the backend restarted) is RECOVERED from its exit marker instead of being
    /// lost; one still alive is left for a future re-attach; one truly gone is abandoned; and one whose handle the
    /// runner cannot answer for from THIS worker is deferred rather than judged — see
    /// <see cref="DeferToTheMintingHostAsync"/>. A run with no handle
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
            .Select(r => new AgentRunReconciliationCandidate { RunId = r.Id, TeamId = r.TeamId, OwnerId = r.OwnerId, ReservationId = r.ReattachReservationId, Epoch = r.FenceEpoch, RunnerHandleJson = r.RunnerHandleJson, ReattachAttempts = r.ReattachAttempts })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var abandoned = 0;
        var recovered = 0;
        var reattached = 0;

        // One budget for the whole batch, so the sweep's cost stays bounded no matter how many candidates need a
        // launch re-discovery. Computed once here rather than per candidate — a per-candidate budget is not a budget.
        var adoptionDeadline = now + AdoptionSweepBudget;

        foreach (var c in candidates)
            switch (await ResolveStaleRunAsync(c, adoptionDeadline, cancellationToken).ConfigureAwait(false))
            {
                case StaleOutcome.Recovered: recovered++; break;
                case StaleOutcome.Abandoned: abandoned++; break;
                case StaleOutcome.Reattached: reattached++; break;
            }

        return (abandoned, recovered, reattached);
    }

    /// <summary>Decide one stale run's fate: probe its durable handle (recover / leave-alone / abandon / defer when the probe cannot be answered from this host), or blind-abandon when there's no usable handle or the probe fails.</summary>
    private async Task<StaleOutcome> ResolveStaleRunAsync(AgentRunReconciliationCandidate candidate, DateTimeOffset adoptionDeadline, CancellationToken cancellationToken)
    {
        var runId = candidate.RunId;
        var reattachAttempts = candidate.ReattachAttempts;
        var durable = ResolveDurableRunner(candidate.RunnerHandleJson, out var handle);

        if (durable is null || handle is null)
        {
            // No usable handle is not the same as no execution. A run that admitted a physical process and crashed
            // before that process became reachable has no handle at all, and blind-abandoning it here is what left a
            // live agent running against a workspace the DB called Failed. The durable attempt still names the
            // execution exactly, so ask for it by identity before deciding anything.
            if (await AdoptAdmittedLaunchAsync(candidate, adoptionDeadline, cancellationToken).ConfigureAwait(false) is { } adopted)
                return adopted;

            return await AbandonAsync(candidate, AgentRunAbandonCause.NoHandle, cancellationToken).ConfigureAwait(false);
        }

        var probe = await ProbeQuietlyAsync(durable, handle, runId, cancellationToken).ConfigureAwait(false);

        if (probe is null)
            return await AbandonAsync(candidate, AgentRunAbandonCause.LeaseLapsed, cancellationToken, durable, handle).ConfigureAwait(false);   // can't probe → kill the maybe-alive orphan, then abandon (don't leave it stuck)

        if (probe.State == SandboxRunState.Exited)
            return await RecoverFromSpoolAsync(candidate, probe.ExitCode ?? -1, cancellationToken).ConfigureAwait(false);

        if (probe.State == SandboxRunState.Indeterminate)
            return await DeferToTheMintingHostAsync(candidate, handle, cancellationToken).ConfigureAwait(false);

        if (probe.State == SandboxRunState.Gone)
            return await AbandonAsync(candidate, AgentRunAbandonCause.ProcessConfirmedDead, cancellationToken, handle: handle).ConfigureAwait(false);   // process already gone — nothing to kill (the handle rides along only so the resource receipts can name its keys)

        // Running: the supervised process is still ALIVE but its worker vanished. Past the re-attach ceiling, KILL
        // it and abandon — a permanently-unattachable-but-alive run must still reach a terminal state, and leaving
        // its process running would keep burning the injected model credential. Otherwise re-attach a fresh observer
        // to resume the live timeline + complete it.
        if (reattachAttempts >= MaxReattachAttempts)
        {
            _logger.LogWarning("AgentRunReconciler: agent run {RunId} is alive but exhausted {Max} re-attach attempts; killing the orphan and abandoning", runId, MaxReattachAttempts);
            return await AbandonAsync(candidate, AgentRunAbandonCause.LeaseLapsed, cancellationToken, durable, handle).ConfigureAwait(false);
        }

        return await ReattachAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recover a run whose physical execution was admitted but never became reachable — the crash-between-launch-and-
    /// acknowledgement window. The durable process attempt names that execution exactly, so this asks the runner the
    /// execution NAMES to re-discover it BY THAT IDENTITY. Four outcomes, and the distinction between them is the
    /// whole point:
    ///
    /// <list type="bullet">
    /// <item>The receipt matches the identity and its process is real → ADOPT: persist the rebuilt handle so the run
    /// is reachable again, and let the next sweep probe it like any other handled run. Deliberately not re-attached
    /// from here: adoption restores the ADDRESS, and the existing probe → recover / re-attach / abandon ladder is what
    /// decides the run's fate, so a recovered handle earns no shortcut past it.</item>
    /// <item>This host's copy of the slot holds no launch request at all → null, and the caller's ordinary abandon
    /// stands. Note what this is NOT: the runner answers absence before it can check host or boot, so on a host that
    /// never held this run's spool it means "nothing HERE", not "nothing anywhere". It is still the honest input to an
    /// abandon — the same conclusion a handle-less run reached before this path existed — but it is not proof that no
    /// process exists.</item>
    /// <item>A launch exists but cannot be adopted from here — bound to a different identity, minted on a foreign
    /// host or boot, or holding a receipt that is unreadable or was consumed without a confirmed execution → null, and
    /// the abandon stands. Nothing is killed on this branch and nothing pretends otherwise: every one of those reasons
    /// is precisely a reason the process cannot be attributed to this run, so a kill would be aimed at a pid this
    /// sweep cannot prove is its own. The run reaches Failed with the same best-effort netns/cgroup teardown any
    /// handle-less abandon gets, and an operator reading the log sees the refusal reason.</item>
    /// <item>The sweep's <see cref="AdoptionSweepBudget"/> is spent and this run HAS a live recorded attempt →
    /// <see cref="StaleOutcome.LeftAlone"/>, so the next sweep tries again. A run with a live recorded attempt is the
    /// one case where abandoning may kill nothing and lose a live agent, so a budget must never convert into a verdict.</item>
    /// </list>
    ///
    /// <para>Null on every OTHER failure is deliberate: this runs inside a fleet sweep whose job is to stop leaving
    /// runs stuck, so an adoption that cannot answer must not become the reason a run stays Running for ever.</para>
    /// </summary>
    private async Task<StaleOutcome?> AdoptAdmittedLaunchAsync(AgentRunReconciliationCandidate candidate, DateTimeOffset adoptionDeadline, CancellationToken cancellationToken)
    {
        var runId = candidate.RunId;
        if (_nativeRecords is not Capture.INativeRecordLaunchPlane launches)
        {
            _logger.LogDebug("AgentRunReconciler: agent run {RunId} has no durable process record plane deployed, so an admitted execution cannot be looked up; the ordinary abandon decides it", runId);

            return null;
        }

        try
        {
            if (await launches.FindAdmittedLaunchAsync(runId, cancellationToken).ConfigureAwait(false) is not { } admitted)
            {
                _logger.LogDebug("AgentRunReconciler: agent run {RunId} has no live recorded process attempt, so there is no admitted execution to adopt", runId);

                return null;
            }

            // The budget is checked HERE — after the one cheap query that says whether a live attempt exists, and
            // before the receipt wait that actually costs time. A run with no attempt still abandons immediately; only
            // a run that might own a live process is deferred, and it is deferred rather than judged.
            if (DateTimeOffset.UtcNow >= adoptionDeadline)
            {
                _logger.LogWarning("AgentRunReconciler: agent run {RunId} records live attempt {AttemptId} but this sweep spent its adoption budget of {Budget}; leaving the run Running for the next sweep rather than abandoning a possibly live execution", runId, admitted.Identity.AttemptId, AdoptionSweepBudget);

                return StaleOutcome.LeftAlone;
            }

            if (_runners.All.FirstOrDefault(runner => runner.Kind == admitted.RunnerKind) is not ISandboxLaunchIdentityRunner adopter)
            {
                _logger.LogDebug("AgentRunReconciler: agent run {RunId} was launched by runner {RunnerKind}, which cannot re-discover a launch by identity, so it cannot be adopted; the ordinary abandon decides it", runId, admitted.RunnerKind);

                return null;
            }

            if (await adopter.AdoptAsync(new SandboxLaunchAdoption(admitted.SpoolKey, admitted.Identity), cancellationToken).ConfigureAwait(false) is not { } discovered)
            {
                _logger.LogInformation("AgentRunReconciler: agent run {RunId} recorded attempt {AttemptId} but this host's launch slot {SpoolKey} holds no launch request, so there is nothing here to adopt", runId, admitted.Identity.AttemptId, admitted.SpoolKey);

                return null;
            }

            var handle = WithRederivedLaunchState(discovered, runId);

            if (await AdoptCandidateHandleAsync(candidate, JsonSerializer.Serialize(handle, AgentJson.Options), cancellationToken).ConfigureAwait(false) != 1)
            {
                _logger.LogInformation("AgentRunReconciler: agent run {RunId} changed under this sweep before its adopted handle could be written, so the worker that moved it decides it", runId);

                return null;
            }

            // Logged, not appended as a run event. An AgentRunEvent inside the liveness window is exactly what
            // excludes a run from the next sweep's candidate set, so a breadcrumb here would buy the operator one line
            // and cost the run a full liveness window of ownerless Running before the probe ladder could reach it.
            _logger.LogWarning("AgentRunReconciler: adopted the admitted execution of agent run {RunId} by attempt {AttemptId} (pid {ProcessId}); its handle was never acknowledged, and the run is addressable again for the next sweep's probe", runId, admitted.Identity.AttemptId, handle.ProcessId);

            return StaleOutcome.LeftAlone;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _logger.LogWarning(failure, "AgentRunReconciler: agent run {RunId} could not adopt an admitted execution by its attempt identity; the ordinary abandon decides it", runId);

            return null;
        }
    }

    /// <summary>
    /// Restore onto an adopted handle the launch-time state the RUNNER cannot know, and only what is genuinely
    /// re-derivable. <c>ProgressLeaseDirectory</c> is: it is a pure function of the run id
    /// (<see cref="LocalProcessRunner.ProgressLeaseDirectoryFor"/>) and is the same path the run's platform endpoint
    /// renews, so without it a re-attaching observer resolves a null lease and its no-progress watchdog watches
    /// nothing.
    ///
    /// <para>What is NOT restored, and why an adopted run is narrower than the one it recovers:
    /// <c>InjectedKeyFingerprint</c> and <c>McpRunToken</c> are minted from a decrypted credential and a one-time
    /// secret that were never persisted, so an adopted run completes marker-only (a re-attach with a mismatched
    /// fingerprint fails capture closed, by design) and cannot re-open its MCP endpoint.
    /// <c>WorkspaceDirectory</c>/<c>WorkspaceBaseSha</c> are a PAIR — the diff capture requires both — and the base
    /// SHA is durable nowhere until the run's own result writes it, while a repo-backed clone's directory is a random
    /// root the launch never recorded. Restoring the directory alone would break the documented "null exactly when the
    /// other is" invariant and still capture no diff, so both stay null and the clone ages out through
    /// <c>IWorkspaceJanitor</c>. <c>AgentRunLogCaptureSessionId</c> is minted per launch and persisted only by the log
    /// capture OPEN — which is downstream of the write that failed — so no row names this launch's session; a session
    /// id recovered from an earlier round would bind the handle to another spool's capture.</para>
    /// </summary>
    private static SandboxHandle WithRederivedLaunchState(SandboxHandle discovered, Guid runId) =>
        discovered with { ProgressLeaseDirectory = LocalProcessRunner.ProgressLeaseDirectoryFor(runId) };

    /// <summary>
    /// The runner could not answer this handle's liveness FROM THIS WORKER (it was minted on another host, whose
    /// process namespace owns the pid). An unanswerable probe is NOT evidence of death, so it deliberately does NOT
    /// take the <see cref="AbandonAsync"/> path a confirmed <see cref="SandboxRunState.Gone"/> takes — that read is
    /// exactly how a live detached run gets destroyed by a worker that never launched it. It is left alone: the sweep
    /// runs every minute on whichever replica Hangfire hands it to, so one landing on the minting host answers the
    /// same handle definitively (Running → re-attach, Exited → recover, Gone → abandon).
    ///
    /// <para>Deferring cannot be unconditional, or a run whose host never returns (a replaced pod) stays Running for
    /// good — trading a rare live-run loss for a routine stuck run. The bound is the handle's OWN wall clock: past
    /// <see cref="SandboxHandle.Deadline"/> no observer can still be legitimately completing the run (the observer
    /// terminates at that instant), which is a ground that needs no pid, so the deferral ends there and the run is
    /// abandoned WITHOUT a kill — this worker cannot reach that host's process. A run launched with NO wall clock
    /// (<c>TimeoutSeconds</c> ≤ 0 ⇒ <see cref="DateTimeOffset.MaxValue"/>) therefore has no such bound and is
    /// deferred until its host returns or an operator cancels it: the residual cost of never guessing.</para>
    /// </summary>
    private async Task<StaleOutcome> DeferToTheMintingHostAsync(AgentRunReconciliationCandidate candidate, SandboxHandle handle, CancellationToken cancellationToken)
    {
        var runId = candidate.RunId;
        if (DateTimeOffset.UtcNow < handle.Deadline)
        {
            _logger.LogInformation("AgentRunReconciler: leaving agent run {RunId} alone — its handle was minted on host {LaunchHost}, whose pid this worker cannot resolve; its deadline {Deadline} has not passed, so a sweep on that host decides", runId, handle.LaunchHost, handle.Deadline);
            return StaleOutcome.LeftAlone;
        }

        _logger.LogWarning("AgentRunReconciler: abandoning agent run {RunId} — its handle's host {LaunchHost} never answered and its deadline {Deadline} has passed, so no observer can still be completing it; its process cannot be reaped from here", runId, handle.LaunchHost, handle.Deadline);
        return await AbandonAsync(candidate, AgentRunAbandonCause.LeaseLapsed, cancellationToken, handle: handle).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-attach a stale-but-alive Running run: atomically reclaim it (bump the fence epoch + re-lease + INCREMENT
    /// the attempt counter, status-guarded) and dispatch <see cref="IAgentRunExecutor.ReattachAsync"/> to resume
    /// tailing + complete it. The fresh lease drops the run out of the candidate set until the re-attaching worker
    /// renews it, so it isn't re-dispatched every sweep; losing the reclaim CAS (another replica won / it already
    /// landed terminal) leaves it alone. The attempt ceiling is enforced by the caller (which has the durable
    /// handle to kill the orphan on the past-ceiling abandon).
    /// </summary>
    private async Task<StaleOutcome> ReattachAsync(AgentRunReconciliationCandidate candidate, CancellationToken cancellationToken)
    {
        var runId = candidate.RunId;
        if (await _runs.ReserveReattachAsync(candidate, cancellationToken).ConfigureAwait(false) is not { } reservation)
            return StaleOutcome.LeftAlone;   // lost the reclaim CAS — another replica won, or it just landed terminal

        await TryAppendEventAsync(runId, AgentEventKind.Warning, ReattachNote, cancellationToken).ConfigureAwait(false);
        _jobs.Enqueue<IAgentRunExecutor>(e => e.ReattachAsync(reservation, CancellationToken.None), HangfireConstants.AgentQueue);

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
    /// abandoned-run event when it transitions, and closes the run's own open native-record attempt/execution
    /// (<paramref name="cause"/> names which ladder branch gave up, for a reader of those rows) — otherwise a phantom
    /// live process would survive in every reader of the record plane even after the Agent Run itself is Failed.
    /// </summary>
    private async Task<StaleOutcome> AbandonAsync(AgentRunReconciliationCandidate candidate, AgentRunAbandonCause cause, CancellationToken cancellationToken, ISandboxDurableRunner? durable = null, SandboxHandle? handle = null)
    {
        var runId = candidate.RunId;
        var transitioned = await TerminalizeCandidateAsync(candidate, AgentRunStatus.Failed, AbandonedError, null, cancellationToken).ConfigureAwait(false);

        // P2 (capture-intent saga): an abandoned attempt died inside (or before) its capture window — every open
        // promise it holds is now permanently unknown. Visible, never silent.
        var settledCaptures = transitioned > 0
            ? await _captureIntents.MarkIndeterminateForRunAsync(runId, cancellationToken).ConfigureAwait(false)
            : 0;

        if (transitioned == 0) return StaleOutcome.LeftAlone;

        // The CAS above just bumped fence_epoch by exactly one, so this is the run's fresh fence — the closer's own
        // fencing is what makes a call here safe even if that read were ever stale.
        await TerminalizeAbandonedHarnessExecutionQuietlyAsync(candidate.TeamId, runId, candidate.Epoch + 1, cause, cancellationToken).ConfigureAwait(false);

        // Before the kill, for the reason the cancel path states: a signal races the orphan's next model call, and a
        // withdrawn lease does not. A no-op unless THIS worker holds the lease — an orphan abandoned by a different
        // worker stopped being able to spend when its own worker's heartbeat stopped, one TTL earlier.
        await RevokeBrokeredCredentialQuietlyAsync(runId).ConfigureAwait(false);

        if (durable is not null && handle is not null)
            await TerminateQuietlyAsync(durable, handle, runId, cancellationToken).ConfigureAwait(false);

        // The CAS above bumped fence_epoch by exactly one, so this is the generation every receipt below is stamped
        // with. WHOSE resources these are decides what may be said about them: a handle minted on another host names
        // a spool, a netns, a cgroup leaf and a clone that exist in namespaces this worker cannot address, and the
        // teardowns below would run against keys that mean nothing here.
        var stamp = new RunCleanupStamp(runId, candidate.Epoch + 1, LocalProcessRunner.CurrentHost, DateTimeOffset.UtcNow);

        if (handle is not null && !LocalProcessRunner.PidAnswerableHere(handle))
            await RecordForeignOrphansAsync(handle, stamp, settledCaptures, cancellationToken).ConfigureAwait(false);
        else
            await ReclaimLocalIsolationAsync(handle, stamp, settledCaptures, cancellationToken).ConfigureAwait(false);

        await RecordLogOwnerLossQuietlyAsync(candidate.TeamId, stamp, cancellationToken).ConfigureAwait(false);

        await TryAppendEventAsync(runId, AgentEventKind.Error, AbandonedError, cancellationToken).ConfigureAwait(false);
        return StaleOutcome.Abandoned;
    }

    /// <summary>
    /// Record — never attempt — the cleanup of a run whose resources live on a host this worker cannot reach. This is
    /// the whole slice: the abandon itself is legitimate (past the handle's own wall clock no observer can still be
    /// completing the run), but every resource it held is standing on <see cref="SandboxHandle.LaunchHost"/>, and the
    /// only honest act available here is to say so in a row addressed to that host's own sweep
    /// (<c>AgentRunOrphanReaper</c>). Before this, the two teardowns below ran anyway against foreign keys and the
    /// miss was swallowed as a best-effort warning — including the egress-subnet lease, which is released on the host
    /// that HOLDS it, so the dead host's lease was never returned to a bounded pool and nothing recorded the loss.
    /// </summary>
    private async Task RecordForeignOrphansAsync(SandboxHandle handle, RunCleanupStamp stamp, int settledCaptures, CancellationToken cancellationToken)
    {
        var receipts = RunCleanupReceipts.ForForeignAbandon(handle, stamp, settledCaptures);

        _logger.LogWarning("AgentRunReconciler: agent run {RunId} was abandoned from host {Here}, but its resources live on {Owner} — recording {Count} cleanup receipt(s) for that host's own sweep instead of a teardown this worker cannot perform", stamp.AgentRunId, stamp.RecordedByHost, handle.LaunchHost, receipts.Count);

        foreach (var receipt in receipts)
            await UpsertQuietlyAsync(receipt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Same-host abandon: run the isolation teardowns exactly as before, and record what they did. The spool and the
    /// workspace clone are deliberately NOT claimed here — they are reclaimed on this host by the spool reaper's and
    /// the workspace janitor's own retention policies, which this path has no authority to pre-empt — so a receipt is
    /// written only for the resources this abandon itself is responsible for freeing. The capture promises are
    /// reported only when this abandon actually settled some: unlike the foreign account, a sweep standing on the
    /// run's own host that moved no intent knows there was nothing outstanding, so it has nothing to say.
    /// </summary>
    private async Task ReclaimLocalIsolationAsync(SandboxHandle? handle, RunCleanupStamp stamp, int settledCaptures, CancellationToken cancellationToken)
    {
        await TearDownEgressNetnsAsync(handle?.EgressNetnsKey, stamp, cancellationToken).ConfigureAwait(false);
        await TearDownCgroupAsync(handle?.CgroupRunKey, stamp, cancellationToken).ConfigureAwait(false);

        if (settledCaptures > 0)
            await UpsertQuietlyAsync(stamp.Completed(RunResourceKind.LogSegments, stamp.RecordedByHost, null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tear down an abandoned run's filtered-egress netns (B3 stability). When the handle NAMES the key the run
    /// provably had one, so the attempt always leaves a receipt — <see cref="RunResourceOutcome.Completed"/>, or
    /// <see cref="RunResourceOutcome.Unknown"/> when this host cannot even attempt it (no <c>ip</c>/<c>nft</c>) or the
    /// attempt threw. When it does not, this is still the runId-keyed BACKSTOP for a run that crashed between the
    /// netns setup and the handle persist (the netns key IS the run id), and a blind backstop that succeeds asserts
    /// nothing — a resource that may never have existed earns no row. A blind backstop that FAILS does: that is the
    /// silent warning this slice exists to replace.
    /// </summary>
    private async Task TearDownEgressNetnsAsync(string? netnsKey, RunCleanupStamp stamp, CancellationToken cancellationToken)
    {
        var declared = netnsKey is { Length: > 0 };
        var key = declared ? netnsKey! : stamp.AgentRunId.ToString("N");

        if (!FilteredEgressNetns.IsSupported)
        {
            if (declared) await UpsertQuietlyAsync(stamp.Unknown(RunResourceKind.EgressSubnet, stamp.RecordedByHost, key, RunCleanupReceipts.UnsupportedCode), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await FilteredEgressNetns.TeardownAsync(key, cancellationToken).ConfigureAwait(false);

            if (declared) await UpsertQuietlyAsync(stamp.Completed(RunResourceKind.EgressSubnet, stamp.RecordedByHost, key), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentRunReconciler: egress-netns teardown for abandoned run {RunId} failed", stamp.AgentRunId);
            await UpsertQuietlyAsync(stamp.Unknown(RunResourceKind.EgressSubnet, stamp.RecordedByHost, key, TeardownFailedCode), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Withdraw an abandoned run's brokered model credential — best-effort: the abandon already stands, and no failure here may change it.</summary>
    private async Task RevokeBrokeredCredentialQuietlyAsync(Guid runId)
    {
        if (_credentialBroker is null) return;

        try { await _credentialBroker.RevokeAsync(runId, "run-abandoned", CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { _logger.LogWarning(exception, "AgentRunReconciler: the brokered model credential for abandoned run {RunId} could not be revoked; it lapses on its own TTL instead", runId); }
    }

    /// <summary>The cgroup parallel of <see cref="TearDownEgressNetnsAsync"/>, with the same declared-vs-backstop rule. A host with no delegated root can attempt nothing, so a declared leaf there is <see cref="RunResourceOutcome.Unknown"/> rather than quietly left.</summary>
    private async Task TearDownCgroupAsync(string? cgroupKey, RunCleanupStamp stamp, CancellationToken cancellationToken)
    {
        var declared = cgroupKey is { Length: > 0 };
        var key = declared ? cgroupKey! : stamp.AgentRunId.ToString("N");

        if (!CgroupResourceLimit.IsSupported || CgroupResourceLimit.CgroupRoot is not { } root)
        {
            if (declared) await UpsertQuietlyAsync(stamp.Unknown(RunResourceKind.Cgroup, stamp.RecordedByHost, key, RunCleanupReceipts.UnsupportedCode), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await CgroupResourceLimit.TeardownAsync(root, key, cancellationToken).ConfigureAwait(false);

            if (declared) await UpsertQuietlyAsync(stamp.Completed(RunResourceKind.Cgroup, stamp.RecordedByHost, key), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentRunReconciler: cgroup teardown for abandoned run {RunId} failed", stamp.AgentRunId);
            await UpsertQuietlyAsync(stamp.Unknown(RunResourceKind.Cgroup, stamp.RecordedByHost, key, TeardownFailedCode), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Close the log streams the dead generation left Open. Its worker was the only thing that could ever have drained
    /// them, so without this they sat Open at a superseded fence and the Room's fold reported "Finalizing" about a
    /// capture that ended when the host did — with no deadline and nothing that could move it.
    ///
    /// <para>Runs immediately after the cleanup receipts, and states the same <see cref="RunCleanupStamp.FenceEpoch"/>
    /// they are stamped with, so the message the operator reads on the stream cites the exact rows this abandon wrote.
    /// It cannot literally share their transaction — the log seam owns its own connection by design, because its
    /// writes bracket provider I/O that must never sit inside a database transaction — so it takes the same shape as
    /// every other statement in this method: the abandon already stands, and no failure here may change it.</para>
    /// </summary>
    private async Task RecordLogOwnerLossQuietlyAsync(Guid teamId, RunCleanupStamp stamp, CancellationToken cancellationToken)
    {
        var request = new AgentRunLogOwnerLossRequest(teamId, stamp.AgentRunId, stamp.FenceEpoch, AgentRunLogOwnerLossRequest.OwnerLostErrorCode);

        try
        {
            var orphaned = await _logs.RecordOwnerLossAsync(request, cancellationToken).ConfigureAwait(false);

            if (orphaned > 0)
                _logger.LogWarning("AgentRunReconciler: agent run {RunId} left {Streams} log stream(s) open at a superseded capture fence; recorded them as {Code} rather than leaving them reported as still finalizing", stamp.AgentRunId, orphaned, AgentRunLogOwnerLossRequest.OwnerLostErrorCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "AgentRunReconciler: could not record log-capture owner loss for abandoned agent run {RunId}; its streams stay Open for a later sweep", stamp.AgentRunId);
        }
    }

    /// <summary>The abandon must stand even if its bookkeeping cannot be written: the run reaching a terminal state is the no-stuck-run guarantee, and a receipt is evidence ABOUT that. A lost write is logged rather than raised, and the owning host's sweep simply never learns about that one resource.</summary>
    private async Task UpsertQuietlyAsync(RunCleanupReceipt receipt, CancellationToken cancellationToken)
    {
        try { await _cleanup.UpsertAsync(receipt, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "AgentRunReconciler: could not record the {Kind} cleanup receipt for agent run {RunId}", receipt.Kind, receipt.AgentRunId);
        }
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
    /// Close the abandoned run's own live native-record execution + any attempt still Running inside it, stamped
    /// with the reconciler's own <paramref name="cause"/> — mirrors AgentRunExecutor.TerminalizeHarnessExecutionAsync
    /// (best-effort and last: the run already reached Failed, and no failure here may change that). A no-op when the
    /// deployed plane does not implement the closing side at all.
    /// </summary>
    private async Task TerminalizeAbandonedHarnessExecutionQuietlyAsync(Guid teamId, Guid runId, long expectedEpoch, AgentRunAbandonCause cause, CancellationToken cancellationToken)
    {
        if (_nativeRecords is not Capture.INativeRecordExecutionPlane executions) return;

        try { await executions.TerminalizeAbandonedAsync(teamId, runId, expectedEpoch, cause, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "AgentRunReconciler: agent run {RunId} harness execution could not be terminalized (cause {Cause}); the row stays live for a later sweep", runId, cause);
        }
    }

    /// <summary>
    /// Close a spool-recovered run's own live native-record execution + any attempt still Running inside it. The
    /// generic "never observed" reason is honest here too: the native plane's own observer never recorded this exit
    /// — the recovery read a completely different signal (the sandbox runner's own exit marker) — so it is the same
    /// closer the executor's forced terminals use, not a reconciler-specific cause.
    /// </summary>
    private async Task TerminalizeRecoveredHarnessExecutionQuietlyAsync(Guid teamId, Guid runId, long expectedEpoch, CancellationToken cancellationToken)
    {
        if (_nativeRecords is not Capture.INativeRecordExecutionPlane executions) return;

        try { await executions.TerminalizeAsync(teamId, runId, expectedEpoch, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "AgentRunReconciler: agent run {RunId} harness execution could not be terminalized after spool recovery; the row stays live for a later sweep", runId);
        }
    }

    /// <summary>
    /// Salvage a run whose durable spool shows it already finished: CAS Running → Succeeded/Failed by the exit
    /// code, with a result + an event noting the recovery. The raw spool output is NOT folded in here — the
    /// reconciler can't decrypt the run's secret to redact it, so it persists only the exit code; the redacted
    /// events the live observer streamed before it died are already in the log.
    /// </summary>
    /// <summary>The run's frozen task envelope (for the S5 contract check) — null on a missing / unparseable payload (fail-open here would be wrong, but an unparseable task also cannot have been graded on the happy path; log-and-null keeps recovery alive).</summary>
    private async Task<AgentTask?> ReadTaskAsync(Guid runId, CancellationToken cancellationToken)
    {
        var taskJson = await _db.AgentRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.TaskJson).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(taskJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<AgentTask>(taskJson, AgentJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "AgentRunReconciler: agent run {RunId} has an unparseable TaskJson — skipping the acceptance-contract check", runId);
            return null;
        }
    }

    private async Task<StaleOutcome> RecoverFromSpoolAsync(AgentRunReconciliationCandidate candidate, int exitCode, CancellationToken cancellationToken)
    {
        var runId = candidate.RunId;
        var result = new AgentRunResult { Status = exitCode == 0 ? AgentRunStatus.Succeeded : AgentRunStatus.Failed, ExitReason = "recovered-from-spool", Error = exitCode == 0 ? null : $"{RecoveredError} The agent exited with code {Sandbox.SandboxExitCode.Describe(exitCode)}." };

        // Completion contract (Slice A1): even on this crash-recovery path, a clean exit can't be called Succeeded while a
        // decision the run raised is still unanswered — re-grade to NeedsReview(NeedsDecision) so the invariant holds here
        // too, mirroring AgentRunService.CompleteCoreAsync. Only a would-be Succeeded needs the lookup.
        if (result.Status == AgentRunStatus.Succeeded)
        {
            var pendingDecisionId = await _ledger.FindBlockingDecisionIdAsync(runId, cancellationToken).ConfigureAwait(false);
            result = AgentCompletionContract.ApplyPendingDecision(result, pendingDecisionId);
        }

        // Acceptance contract (S5): the same every-terminal-path mirror — a contract-bearing task recovered from the
        // spool has no published branch to grade (the workspace died with the worker), so it fails CLOSED rather than
        // landing Succeeded ungraded because the backend restarted at the right moment. A1 above still wins (a
        // NeedsDecision re-grade is no longer a would-be Succeeded).
        if (result.Status == AgentRunStatus.Succeeded && await ReadTaskAsync(runId, cancellationToken).ConfigureAwait(false) is { } spoolTask && AgentAcceptanceContract.RequiresGrade(spoolTask))
        {
            _logger.LogWarning("AgentRunReconciler: agent run {RunId} carries an acceptance contract but was recovered from the spool with no gradable branch — failing closed", runId);

            result = AgentAcceptanceContract.FailClosed(result, "no-branch-or-repo (recovered from spool — the branch never published)");
        }

        var status = result.Status;
        var error = result.Error;
        var resultJson = JsonSerializer.Serialize(result, AgentJson.Options);

        var transitioned = await TerminalizeCandidateAsync(candidate, status, error, resultJson, cancellationToken).ConfigureAwait(false);
        if (transitioned == 0) return StaleOutcome.LeftAlone;

        // Only the winner can invalidate capture promises. A losing stale probe has no authority over its successor.
        await _captureIntents.MarkIndeterminateForRunAsync(runId, cancellationToken).ConfigureAwait(false);

        // The CAS above just bumped fence_epoch by exactly one, so this is the run's fresh fence. The spool's exit
        // code lands on the Agent Run's own result above; it never reaches the native record plane, whose own
        // observer genuinely never saw this process exit.
        await TerminalizeRecoveredHarnessExecutionQuietlyAsync(candidate.TeamId, runId, candidate.Epoch + 1, cancellationToken).ConfigureAwait(false);

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

    private async Task<int> TerminalizeCandidateAsync(AgentRunReconciliationCandidate candidate, AgentRunStatus status, string? error, string? resultJson, CancellationToken cancellationToken)
    {
        var duration = AgentRunLiveness.LeaseDuration;
        return await _db.Database.ExecuteSqlInterpolatedAsync($"WITH locked AS MATERIALIZED (SELECT id FROM agent_run WHERE id = {candidate.RunId} FOR UPDATE) UPDATE agent_run AS target SET status = {status.ToString()}, error = {error}, result_jsonb = COALESCE(CAST({resultJson} AS jsonb), target.result_jsonb), completed_at = clock_timestamp(), fence_epoch = target.fence_epoch + 1 FROM locked WHERE target.id = locked.id AND target.status = 'Running' AND target.owner_id IS NOT DISTINCT FROM {candidate.OwnerId} AND target.reattach_reservation_id IS NOT DISTINCT FROM {candidate.ReservationId} AND target.fence_epoch = {candidate.Epoch} AND target.runner_handle IS NOT DISTINCT FROM CAST({candidate.RunnerHandleJson} AS jsonb) AND target.reattach_attempts = {candidate.ReattachAttempts} AND (target.lease_expires_at <= clock_timestamp() OR (target.lease_expires_at IS NULL AND COALESCE(target.heartbeat_at, target.started_at, target.created_date) <= clock_timestamp() - {duration}))", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Write an adopted handle onto a run under the SAME scanned identity <see cref="TerminalizeCandidateAsync"/>
    /// terminalizes under: this row still Running, still held by the owner and fence this sweep observed, its lease
    /// still lapsed, and — the clause that matters most here — <c>runner_handle</c> still exactly what was scanned,
    /// which for an unacknowledged launch is NULL. So a worker that legitimately reclaimed the run and wrote its own
    /// handle in the meantime is never overwritten by this one.
    ///
    /// <para>It deliberately does NOT bump the fence, release the owner, or extend the lease. Adoption restores an
    /// ADDRESS and claims nothing else: the run stays a candidate, and the next sweep's ordinary probe ladder decides
    /// its fate from the handle this write just made readable. Bumping the fence here would invalidate the very
    /// attempt row whose identity was just used to find the process.</para>
    ///
    /// <para>The service's unfenced <c>SetRunnerHandleAsync(runId, …)</c> is deliberately not used: it refuses any run
    /// that still names an owner, which is precisely the state a crashed worker leaves behind.</para>
    /// </summary>
    private async Task<int> AdoptCandidateHandleAsync(AgentRunReconciliationCandidate candidate, string handleJson, CancellationToken cancellationToken)
    {
        var duration = AgentRunLiveness.LeaseDuration;
        return await _db.Database.ExecuteSqlInterpolatedAsync($"WITH locked AS MATERIALIZED (SELECT id FROM agent_run WHERE id = {candidate.RunId} FOR UPDATE) UPDATE agent_run AS target SET runner_handle = CAST({handleJson} AS jsonb) FROM locked WHERE target.id = locked.id AND target.status = 'Running' AND target.owner_id IS NOT DISTINCT FROM {candidate.OwnerId} AND target.reattach_reservation_id IS NOT DISTINCT FROM {candidate.ReservationId} AND target.fence_epoch = {candidate.Epoch} AND target.runner_handle IS NOT DISTINCT FROM CAST({candidate.RunnerHandleJson} AS jsonb) AND target.reattach_attempts = {candidate.ReattachAttempts} AND (target.lease_expires_at <= clock_timestamp() OR (target.lease_expires_at IS NULL AND COALESCE(target.heartbeat_at, target.started_at, target.created_date) <= clock_timestamp() - {duration}))", cancellationToken).ConfigureAwait(false);
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
                await CancelOrphanedQueuedAsync(run.Id, OrphanedParentTerminalError, cancellationToken).ConfigureAwait(false);
            else // The bounded selection already established staleness using the database clock.
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

    /// <summary>Cancel a still-Queued branch agent run orphaned under a now-terminal parent workflow run, via the Queued-guarded CAS (a worker that just claimed it loses 0 rows and is left alone). Returns 1 when it transitioned, else 0. A failure is logged + retried next sweep — never throws out of the sweep.</summary>
    private async Task<int> CancelOrphanedQueuedAsync(Guid runId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _runs.CancelQueuedAsync(runId, reason, cancellationToken).ConfigureAwait(false))
                return 0;

            await TryAppendEventAsync(runId, AgentEventKind.Error, reason, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("AgentRunReconciler: cancelled orphaned queued agent run {RunId} ({Reason})", runId, reason);
            return 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentRunReconciler: failed to cancel orphaned queued agent run {RunId}; will retry next sweep", runId);
            return 0;
        }
    }

    /// <summary>Resume the workflow parked on a terminal agent run, via the same notifier the executor uses. A failure is logged + retried next sweep — never throws out of the sweep.</summary>
    private async Task<int> TryResumeParentAsync(Guid runId, AgentRunStatus status, CancellationToken cancellationToken)
    {
        try
        {
            var token = runId.ToString();
            var pendingIds = await _db.WorkflowRunWait.AsNoTracking().Where(w => w.WaitKind == WorkflowWaitKinds.AgentRun && w.Token == token && w.Status == WorkflowWaitStatuses.Pending
                && _db.AgentRun.Any(a => a.Id == runId && a.WorkflowRunId == w.RunId && _db.WorkflowRun.Any(p => p.Id == w.RunId && p.TeamId == a.TeamId))).Select(w => w.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (pendingIds.Count == 0) return 0;
            await _notifier.NotifyCompletedAsync(runId, cancellationToken).ConfigureAwait(false);
            // NotifyCompletedAsync is best effort. A normal return alone proves nothing; the durable wait is the acknowledgement.
            if (!await _db.WorkflowRunWait.AsNoTracking().AnyAsync(w => pendingIds.Contains(w.Id) && w.Status == WorkflowWaitStatuses.Resolved, cancellationToken).ConfigureAwait(false)) return 0;
            _logger.LogInformation("AgentRunReconciler: acknowledged a terminal agent wait for {RunId} ({Status}); the parent may still have other pending waits", runId, status);
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
            _jobs.Enqueue<IAgentRunExecutor>(e => e.ExecuteAsync(runId, CancellationToken.None), HangfireConstants.AgentQueue);

            _logger.LogInformation("AgentRunReconciler: re-dispatched stuck queued agent run {RunId} (created {CreatedDate:o})", runId, createdDate);
            return 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentRunReconciler: failed to re-dispatch queued agent run {RunId}; will retry next sweep", runId);
            return 0;
        }
    }

    /// <summary>Select eligible waits before the cap and persist retry order before invoking any notifier. A crashed worker leaves a retryable timestamp, never a false acknowledgement.</summary>
    private async Task<List<Guid>> PendingAgentRunWaitIdsAsync(CancellationToken cancellationToken)
    {
        if (_db.Database.CurrentTransaction != null) throw new InvalidOperationException("Agent wait recovery requires an independent scope without an ambient transaction.");
        var terminalStatuses = Enum.GetValues<AgentRunStatus>().Where(AgentRunStateMachine.IsTerminal).Select(s => s.ToString()).ToArray();
        var window = AgentRunLiveness.Window;
        // The notifier uses this canonical token and parent identity too. Never cast an untrusted token to UUID.
        // A short retry floor avoids immediately reselecting in-flight callbacks in another worker. This is not an ownership lease.
        var ids = await _db.Database.SqlQuery<Guid>($"""
            WITH candidates AS MATERIALIZED (
                SELECT w.id
                FROM workflow_run_wait w
                JOIN agent_run a ON w.token = a.id::text AND w.run_id = a.workflow_run_id
                JOIN workflow_run p ON p.id = w.run_id AND p.team_id = a.team_id
                WHERE w.wait_kind = {WorkflowWaitKinds.AgentRun} AND w.status = {WorkflowWaitStatuses.Pending}
                  AND (w.last_agent_recovery_attempt_at IS NULL OR w.last_agent_recovery_attempt_at < clock_timestamp() - interval '5 seconds')
                  AND (a.status = ANY({terminalStatuses}) OR (a.status = 'Queued' AND
                       (a.created_date < clock_timestamp() - {window} OR p.status IN ('Cancelled', 'Failure', 'Success'))))
                ORDER BY w.last_agent_recovery_attempt_at ASC NULLS FIRST, w.created_at, w.id
                LIMIT {BatchSize} FOR UPDATE OF w SKIP LOCKED
            )
            UPDATE workflow_run_wait w SET last_agent_recovery_attempt_at = clock_timestamp()
            FROM candidates c WHERE w.id = c.id
            RETURNING w.token::uuid AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Distinct().ToList();
    }

    /// <summary>Append one reconciler-authored event (abandonment / recovery / re-attach note) so the live log / replay timeline shows what happened. Best-effort — a logging failure doesn't undo the transition.</summary>
    private async Task TryAppendEventAsync(Guid runId, AgentEventKind kind, string text, CancellationToken cancellationToken)
    {
        try
        {
            await _runs.AppendSystemEventAsync(runId, new AgentEvent { Kind = kind, Text = text }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
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

    /// <summary>Queued branch-agent runs cancelled because their parent workflow run was terminal AND no wait referenced them — the uncollectable-leak backstop for a suspension that committed the Queued run but crashed before its wait (the run would otherwise sit Queued forever, counted against the admission cap).</summary>
    public int CancelledQueuedUnderTerminalParent { get; init; }

    /// <summary>Queued branch-agent runs cancelled because they had NO parent workflow run AND no wait referenced them — the parentless-orphan leak backstop (no other sweep or reclaim path can collect a parentless stuck-Queued run).</summary>
    public int CancelledParentlessQueued { get; init; }

    /// <summary>Running runs flipped to Failed because their worker vanished (stale heartbeat + no events) and they were not recoverable.</summary>
    public int MarkedAbandonedFromRunning { get; init; }

    /// <summary>Running runs salvaged from their durable spool — the agent had already finished while its live observer was gone.</summary>
    public int RecoveredFromSpool { get; init; }

    /// <summary>Still-alive Running runs whose worker vanished — re-claimed + a re-attach worker dispatched to resume the live timeline and complete them.</summary>
    public int ReattachedStaleRunning { get; init; }

    /// <summary>Workflow runs resumed off a terminal agent run that hadn't propagated its completion (crash / failed notify).</summary>
    /// <summary>Terminal agents with an observed persisted wait acknowledgement; a parent can still have other pending waits.</summary>
    public int ResumedStalledParents { get; init; }

    /// <summary>Stuck-Queued agent runs whose dispatch was lost and were re-enqueued to the executor.</summary>
    public int ReDispatchedQueued { get; init; }

    public int Total => CancelledRunningUnderTerminalParent + CancelledQueuedUnderTerminalParent + CancelledParentlessQueued + MarkedAbandonedFromRunning + RecoveredFromSpool + ReattachedStaleRunning + ResumedStalledParents + ReDispatchedQueued;
}
