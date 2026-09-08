using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;

public sealed partial class TaskLaunchBenchmarkCellRunner
{
    private const int DrivePollIntervalMs = 200;

    /// <summary>
    /// <see cref="LaunchAsync"/>, guarded against the ONE known way it can leave an orphan behind: the launch's
    /// <c>WorkSession</c> + <c>WorkflowRun</c> commit in a SINGLE <c>SaveChangesAsync</c>
    /// (<c>RunFromSnapshotStarter.StageAsync</c>) with NO ambient transaction — this runner calls
    /// <see cref="ITaskLaunchService"/> directly, never through the Mediator <c>ICommand</c> pipeline that
    /// <c>TransactionalBehavior</c> wraps — so a LATER step in the same call (route/purpose stamping, the queued-run
    /// ledger write, or the immediate post-commit dispatch <see cref="IPostCommitActions.RunAfterCommitAsync"/> runs
    /// inline when it finds no open transaction) throwing leaves that WorkSession/WorkflowRun durably committed with
    /// no <see cref="LaunchTaskResult"/> ever returned to retire it. On exactly that shape of failure, recover the
    /// newest run SCOPED TO <paramref name="fixture"/>'s own repository this borrowed Owner could have created since
    /// it was staged, and retire its session artifacts before re-throwing — see <see cref="RecoverOrphanedLaunchAsync"/>.
    /// </summary>
    private async Task<LaunchTaskResult> LaunchOrRecoverAsync(BenchmarkTask task, BenchmarkMode mode, BenchmarkExecutionContext context, StagedFixture fixture, CancellationToken cancellationToken)
    {
        var attemptStartedAt = DateTimeOffset.UtcNow;

        try
        {
            return await LaunchAsync(task, mode, context, fixture, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecoverOrphanedLaunchAsync(context.TeamId, fixture.ActorUserId, fixture.RepositoryId, attemptStartedAt, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Build the launch request from the cell's task + arm + selection and enter through the REAL
    /// <see cref="ITaskLaunchService"/> — the one call this whole instrument exists to prove happens. The arm
    /// picks <see cref="TaskLaunchRequest.RequestedEffort"/> (null for <see cref="BenchmarkMode.TaskLaunchAuto"/>
    /// — the router's own classify contract); a <c>null</c> selection folds to Launch's OWN safe defaults exactly
    /// like a real un-overridden launch, mirroring how the direct instrument's <c>BuildAgentTask</c> treats a
    /// null selection as "the deterministic default", not a caller error. Its own fresh scope.
    ///
    /// <para><b>Always Shadow completion mode.</b> A null <c>CompletionMode</c> inherits the platform default, which
    /// (since #1774/C5) is Enforced for the supervisor lane — an unbackable Success claim PARKS a Deep/Auto cell
    /// instead of terminaling it, because <see cref="BenchmarkTask"/> authors no <c>AcceptanceChecks</c> for the
    /// completion-contract system to back a claim with. This instrument grades independently via its OWN objective
    /// oracle (<see cref="BenchmarkTaskGrading.GradeAsync"/>) regardless of completion-contract enforcement, so
    /// Shadow is correct here — the SAME opt-out <c>SupervisorProjectionFlowTests</c> declares for the identical
    /// reason.</para>
    /// </summary>
    private Task<LaunchTaskResult> LaunchAsync(BenchmarkTask task, BenchmarkMode mode, BenchmarkExecutionContext context, StagedFixture fixture, CancellationToken cancellationToken)
    {
        var selection = context.Selection;

        var request = new TaskLaunchRequest
        {
            TeamId = context.TeamId,
            ActorUserId = fixture.ActorUserId,
            SurfaceKind = TaskLaunchSurfaceKinds.Repo,
            TaskText = task.Goal,
            RepositoryId = fixture.RepositoryId,
            RequestedEffort = BenchmarkModeEffort.RequestedEffortFor(mode),
            Autonomy = selection?.Autonomy?.ToString(),
            CompletionMode = WorkflowDefinition.CompletionModeShadow,
            Purpose = WorkflowRunPurposes.Qualification,
            CapsOverride = selection?.MaxCostUsd is { } maxCostUsd ? new RouteCaps { MaxCostUsd = maxCostUsd } : null,
            Overrides = new TaskExecutionOverrides
            {
                Harness = selection?.Harness ?? task.Harness,
                Model = selection?.Model,
                ModelCredentialId = selection?.ModelCredentialId,
                ModelCredentialModelId = selection?.ModelCredentialModelId,
                RunnerKind = Sandbox.SandboxKinds.Local,
                TimeoutSeconds = task.TimeoutSeconds,
                OutputReviewMode = selection?.OutputReviewMode ?? ReviewMode.None,
                ReviewerModelId = selection?.ReviewerModelId,
                ReviseRounds = selection?.MaxReviseRounds,
            },
        };

        return InFreshScopeAsync(scope => scope.Resolve<ITaskLaunchService>().LaunchAsync(request, cancellationToken));
    }

    /// <summary>
    /// Best-effort recovery for the post-commit-throw shape documented on <see cref="LaunchOrRecoverAsync"/>. Identifies
    /// the orphan by the tightest key available WITHOUT relying on <see cref="Persistence.Entities.WorkflowRun.Purpose"/>
    /// (the very stamp a failure here may have pre-empted): the newest <c>WorkflowRun</c> sourced from a snapshot launch,
    /// actored by this cell's borrowed Owner, in this team, SCOPED TO <paramref name="repositoryId"/> — created at or
    /// after <paramref name="attemptStartedAt"/>. The repository scope is load-bearing, not redundant: the borrowed
    /// Owner is a REAL team member who can launch a genuine task in this same team while a cell is mid-flight, and
    /// without this scope that genuine run — newer, same actor, same team — would look like the tightest match and
    /// get its own real session silently archived and its run mislabelled Qualification. <paramref name="repositoryId"/>
    /// is this cell's own freshly-minted, per-invocation fixture repository (<see cref="StagedFixture.RepositoryId"/>),
    /// which a genuine launch can never reference, so it is the one key a real concurrent launch cannot share.
    /// Re-stamps <c>Purpose</c> when it didn't land (so the run also drops out of the team Runs index) and retires the
    /// session artifacts exactly like a normal completed cell (<see cref="RetireSessionArtifactsAsync"/>). Internal
    /// (not private) so this is unit/integration-pinned directly (InternalsVisibleTo) by seeding the exact orphan
    /// shape, since no existing seam injects the underlying post-commit fault itself. Never throws — the caller
    /// re-raises the ORIGINAL launch exception regardless.
    /// </summary>
    internal async Task RecoverOrphanedLaunchAsync(Guid teamId, Guid actorUserId, Guid repositoryId, DateTimeOffset attemptStartedAt, CancellationToken cancellationToken)
    {
        try
        {
            await InFreshScopeAsync(async scope =>
            {
                var db = scope.Resolve<CodeSpaceDbContext>();

                var orphan = await db.WorkflowRun.AsNoTracking()
                    .Where(r => r.TeamId == teamId && r.ActorId == actorUserId && r.SourceType == WorkflowRunSourceTypes.Snapshot && r.ScopeRepositoryIds.Contains(repositoryId) && r.CreatedDate >= attemptStartedAt)
                    .OrderByDescending(r => r.CreatedDate).ThenByDescending(r => r.Id)
                    .Select(r => new { r.Id, r.SessionId, r.Purpose })
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

                if (orphan?.SessionId is not { } sessionId) return;

                if (orphan.Purpose == null)
                    await db.WorkflowRun.Where(r => r.Id == orphan.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.Purpose, WorkflowRunPurposes.Qualification), cancellationToken).ConfigureAwait(false);

                await RetireSessionArtifactsAsync(db, sessionId, cancellationToken).ConfigureAwait(false);

                _logger.LogWarning("TaskLaunchBenchmarkCellRunner: recovered an orphaned qualification launch (run {RunId}, session {SessionId}) after a post-commit failure", orphan.Id, sessionId);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TaskLaunchBenchmarkCellRunner: could not recover an orphaned qualification launch for team {TeamId}", teamId);
        }
    }

    /// <summary>
    /// Walk <paramref name="runId"/> to a terminal <c>WorkflowRunStatus</c> using the SAME production seams a
    /// background worker would (<see cref="IWorkflowEngine"/> / <see cref="IAgentRunExecutor"/> /
    /// <see cref="IWorkflowResumeService"/>), called INLINE — each in its OWN fresh scope (see the class's
    /// scope-discipline note) — so this cell needs no Hangfire worker present, exactly the substitution the direct
    /// instrument already makes for its own single <c>AgentRunExecutor</c> call, generalized to a whole workflow
    /// run. Idempotent by construction: <c>ExecuteRunAsync</c> is documented safe to re-call, and re-driving an
    /// already-resolved wait is a no-op on both the executor and resume sides — so a real background worker racing
    /// this loop in production is harmless, never a double delivery.
    ///
    /// <para><b>Known limitation.</b> This loop only knows how to advance <see cref="WorkflowWaitKinds.AgentRun"/>
    /// and <see cref="WorkflowWaitKinds.SupervisorDecision"/> waits (see <see cref="DriveOnePendingWaveAsync"/>). A
    /// Deep supervisor may instead park on <c>ask_human</c> (<see cref="WorkflowWaitKinds.Action"/>) — nothing ever
    /// resolves that wait here, so rather than silently burning the whole drive budget until <paramref name="deadline"/>
    /// forces a <see cref="TimeoutException"/>, <see cref="FailFastOnUnadvanceableWaitAsync"/> detects the parked
    /// wait immediately and fails with the wait kind named. The corpus loop classifies either exception identically
    /// (infra-errored, excluded from the solve denominator) — this only saves the wasted wall-clock.</para>
    /// </summary>
    private async Task DriveToTerminalAsync(Guid runId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        await ExecuteEngineOnceAsync(runId, cancellationToken).ConfigureAwait(false);

        var executedAgentRunIds = new HashSet<Guid>();

        while (!await IsTerminalAsync(runId, cancellationToken).ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"TaskLaunch qualification cell (run {runId}) did not reach a terminal WorkflowRun status within its drive budget.");

            var progressed = await DriveOnePendingWaveAsync(runId, executedAgentRunIds, cancellationToken).ConfigureAwait(false);

            await ExecuteEngineOnceAsync(runId, cancellationToken).ConfigureAwait(false);

            if (progressed) continue;

            await FailFastOnUnadvanceableWaitAsync(runId, cancellationToken).ConfigureAwait(false);

            await Task.Delay(DrivePollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// When a poll drives NOTHING (<see cref="DriveOnePendingWaveAsync"/> returned false), that is USUALLY a
    /// transient race — the engine hasn't yet materialized the next wait — which the caller's own poll-and-retry
    /// absorbs. The one case that is NOT transient: the run has already parked on a wait kind this loop will NEVER
    /// learn to advance (only <see cref="WorkflowWaitKinds.AgentRun"/> and <see cref="WorkflowWaitKinds.SupervisorDecision"/>
    /// self-drive). Failing fast here turns that dead wait into an immediate, NAMED fault instead of silently
    /// polling until <see cref="DriveToTerminalAsync"/>'s deadline forces a generic <see cref="TimeoutException"/>.
    /// </summary>
    private async Task FailFastOnUnadvanceableWaitAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (await PendingUnadvanceableWaitKindAsync(runId, cancellationToken).ConfigureAwait(false) is not { } stuckKind) return;

        throw new InvalidOperationException($"TaskLaunch qualification cell (run {runId}) parked on a '{stuckKind}' wait this drive loop has no seam to advance (only AgentRun and SupervisorDecision waits self-drive) — failing fast instead of burning the drive budget.");
    }

    /// <summary>The wait kind of a PENDING wait <see cref="DriveOnePendingWaveAsync"/> has no seam for (see <see cref="IsAdvanceableWaitKind"/>), or null when every pending wait is one this loop already knows how to drive (the ordinary in-flight case).</summary>
    private Task<string?> PendingUnadvanceableWaitKindAsync(Guid runId, CancellationToken cancellationToken) =>
        InFreshScopeAsync(async scope =>
        {
            var pendingKinds = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending)
                .Select(w => w.WaitKind)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            return pendingKinds.FirstOrDefault(k => !IsAdvanceableWaitKind(k));
        });

    /// <summary>The exhaustive set of wait kinds <see cref="DriveOnePendingWaveAsync"/> knows how to drive. Internal (not private) so the fail-fast boundary is unit-pinned directly (InternalsVisibleTo) with no DbContext needed.</summary>
    internal static bool IsAdvanceableWaitKind(string waitKind) => waitKind is WorkflowWaitKinds.AgentRun or WorkflowWaitKinds.SupervisorDecision;

    private Task ExecuteEngineOnceAsync(Guid runId, CancellationToken cancellationToken) =>
        InFreshScopeAsync(scope => scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, cancellationToken));

    private Task<bool> IsTerminalAsync(Guid runId, CancellationToken cancellationToken) =>
        InFreshScopeAsync(async scope =>
        {
            var status = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId)
                .Select(r => (WorkflowRunStatus?)r.Status).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            return status is WorkflowRunStatus.Success or WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled;
        });

    /// <summary>
    /// Drive every suspension this runner knows how to advance directly: an <c>agent.run</c> node's parked
    /// <see cref="WorkflowWaitKinds.AgentRun"/> wait (execute its agent run — covers a single agent, a plan-map
    /// fan-out's parallel wave, and every agent a Deep supervisor spawns, since all three park the SAME wait kind
    /// on this same top-level run), and a Deep supervisor's <see cref="WorkflowWaitKinds.SupervisorDecision"/>
    /// self-advance. Returns whether anything actually ran, so the caller only sleeps when truly idle.
    /// </summary>
    private async Task<bool> DriveOnePendingWaveAsync(Guid runId, HashSet<Guid> executedAgentRunIds, CancellationToken cancellationToken)
    {
        var progressed = false;

        foreach (var agentRunId in await PendingAgentRunIdsAsync(runId, cancellationToken).ConfigureAwait(false))
        {
            if (!executedAgentRunIds.Add(agentRunId)) continue;

            await ExecuteAgentRunOnceAsync(agentRunId, cancellationToken).ConfigureAwait(false);
            progressed = true;
        }

        foreach (var waitId in await PendingSupervisorAdvanceWaitIdsAsync(runId, cancellationToken).ConfigureAwait(false))
        {
            await ResumeSupervisorAdvanceOnceAsync(runId, waitId, cancellationToken).ConfigureAwait(false);
            progressed = true;
        }

        return progressed;
    }

    private Task ExecuteAgentRunOnceAsync(Guid agentRunId, CancellationToken cancellationToken) =>
        InFreshScopeAsync(scope => scope.Resolve<IAgentRunExecutor>().ExecuteAsync(agentRunId, cancellationToken));

    private Task ResumeSupervisorAdvanceOnceAsync(Guid runId, Guid waitId, CancellationToken cancellationToken) =>
        InFreshScopeAsync(scope => scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, cancellationToken));

    private Task<List<Guid>> PendingAgentRunIdsAsync(Guid runId, CancellationToken cancellationToken) =>
        InFreshScopeAsync(async scope =>
        {
            var tokens = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
                .Select(w => w.Token)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            return tokens.Where(t => Guid.TryParse(t, out _)).Select(Guid.Parse).ToList();
        });

    private Task<List<Guid>> PendingSupervisorAdvanceWaitIdsAsync(Guid runId, CancellationToken cancellationToken) =>
        InFreshScopeAsync(scope =>
            scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision && w.Status == WorkflowWaitStatuses.Pending)
                .Select(w => w.Id)
                .ToListAsync(cancellationToken));
}
