using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;

public sealed partial class TaskLaunchBenchmarkCellRunner
{
    private const int DrivePollIntervalMs = 200;

    /// <summary>
    /// Build the launch request from the cell's task + arm + selection and enter through the REAL
    /// <see cref="ITaskLaunchService"/> — the one call this whole instrument exists to prove happens. The arm
    /// picks <see cref="TaskLaunchRequest.RequestedEffort"/> (null for <see cref="BenchmarkMode.TaskLaunchAuto"/>
    /// — the router's own classify contract); a <c>null</c> selection folds to Launch's OWN safe defaults exactly
    /// like a real un-overridden launch, mirroring how the direct instrument's <c>BuildAgentTask</c> treats a
    /// null selection as "the deterministic default", not a caller error. Its own fresh scope.
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
            Overrides = new TaskExecutionOverrides
            {
                Harness = selection?.Harness ?? task.Harness,
                Model = selection?.Model,
                ModelCredentialId = selection?.ModelCredentialId,
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
    /// Walk <paramref name="runId"/> to a terminal <c>WorkflowRunStatus</c> using the SAME production seams a
    /// background worker would (<see cref="IWorkflowEngine"/> / <see cref="IAgentRunExecutor"/> /
    /// <see cref="IWorkflowResumeService"/>), called INLINE — each in its OWN fresh scope (see the class's
    /// scope-discipline note) — so this cell needs no Hangfire worker present, exactly the substitution the direct
    /// instrument already makes for its own single <c>AgentRunExecutor</c> call, generalized to a whole workflow
    /// run. Idempotent by construction: <c>ExecuteRunAsync</c> is documented safe to re-call, and re-driving an
    /// already-resolved wait is a no-op on both the executor and resume sides — so a real background worker racing
    /// this loop in production is harmless, never a double delivery.
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

            if (!progressed) await Task.Delay(DrivePollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

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
