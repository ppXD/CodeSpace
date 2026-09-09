using Autofac;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Exceptions;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Commands.Tasks;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;

/// <summary>
/// Default <see cref="ITaskLaunchBenchmarkCellRunner"/> — a flat pipeline (Rule 4): stage the pre-staged fixture
/// directory as a local git origin → launch through the real <c>ITaskLaunchService</c> → drive the resulting
/// workflow run to a terminal state → reconstruct the produced diff onto the fixture → grade it independently →
/// fold everything into a <see cref="BenchmarkResult"/>. Every ad-hoc resource the cell created — the fixture
/// repository, its provider instance, and the launch's <c>WorkSession</c>/<c>Conversation</c> — is always retired,
/// success or failure (see <see cref="RetireFixtureResourcesAsync"/>).
///
/// <para><b>Scope discipline:</b> every operation that touches <c>CodeSpaceDbContext</c> or drives the engine /
/// executor / resume service runs in its OWN freshly-opened <see cref="ILifetimeScope"/> (<see cref="InFreshScopeAsync"/>)
/// — never the constructor's own scope, and never shared across calls. This mirrors exactly how
/// <c>WorkflowEngine</c> itself opens a child scope per parallel node, and how a real Hangfire worker resolves a
/// fresh scope per job: reusing ONE <c>CodeSpaceDbContext</c> across the Launch call and the engine-driving calls
/// below would let EF's identity map hand back a <c>WorkflowRun</c> tracked with a now-stale <c>xmin</c> (bumped by
/// an unrelated <c>ExecuteUpdateAsync</c> elsewhere in the same launch), and the walker's later tracked save of
/// that same run would fail with a spurious <c>DbUpdateConcurrencyException</c> — reproduced and fixed during this
/// slice's own red-first pass, not a theoretical concern.
/// </summary>
public sealed partial class TaskLaunchBenchmarkCellRunner : ITaskLaunchBenchmarkCellRunner, IScopedDependency
{
    private readonly ILifetimeScope _scope;
    private readonly IBenchmarkGraderRegistry _graders;
    private readonly Sandbox.ISandboxRunnerRegistry _runners;
    private readonly ILogger<TaskLaunchBenchmarkCellRunner> _logger;

    public TaskLaunchBenchmarkCellRunner(ILifetimeScope scope, IBenchmarkGraderRegistry graders, Sandbox.ISandboxRunnerRegistry runners, ILogger<TaskLaunchBenchmarkCellRunner> logger)
    {
        _scope = scope;
        _graders = graders;
        _runners = runners;
        _logger = logger;
    }

    public async Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, BenchmarkExecutionContext context, CancellationToken cancellationToken)
    {
        var durable = context.CheckpointSink is not null || context.Checkpoints.Count > 0;
        if (durable && (context.CheckpointSink is null || context.Completion is null)) throw new DurableBenchmarkObservationException("TaskLaunch durable recovery requires checkpoint and completion sinks.");
        var fixture = await StageFixtureRepositoryAsync(task, context, cancellationToken).ConfigureAwait(false);
        LaunchTaskResult? launched = null;
        var resultSettled = false;

        try
        {
            launched = await LaunchOrRecoverAsync(task, mode, context, fixture, cancellationToken).ConfigureAwait(false);

            await DriveToTerminalAsync(launched.RunId, DriveDeadline(task), cancellationToken).ConfigureAwait(false);

            var attempts = await LoadAgentRunsAsync(launched.RunId, cancellationToken).ConfigureAwait(false);

            if (attempts.Count == 0)
                throw new InvalidOperationException($"TaskLaunch cell {launched.RunId} (arm {mode}) reached a terminal workflow run with no AgentRun ever created — nothing to grade.");

            await ReconstructWorkspaceAsync(context.WorkspaceDirectory, attempts, cancellationToken).ConfigureAwait(false);

            var grade = await BenchmarkTaskGrading.GradeAsync(_graders, _runners, new BenchmarkTaskGradingRequest { Task = task, WorkspaceDirectory = context.WorkspaceDirectory, TeamId = context.TeamId, ProducerModel = ProducerModelOf(context.Selection, attempts) }, cancellationToken).ConfigureAwait(false);

            var completionMode = await LoadCompletionEnforcementModeAsync(launched.RunId, cancellationToken).ConfigureAwait(false);

            var result = BuildResult(task, mode, launched, attempts, grade, ObservedModelOf(attempts), completionMode);
            if (context.Completion is not null) await context.Completion.CompleteAsync(result, CancellationToken.None).ConfigureAwait(false);
            resultSettled = true;
            return result;
        }
        catch (Exception exception) when (durable && exception is not OperationCanceledException and not DurableBenchmarkObservationException)
        {
            throw new DurableBenchmarkObservationException($"TaskLaunch run {launched?.RunId} has durable recovery identity but did not reach terminal settlement; resume must adopt it instead of recording infra or launching again.", exception);
        }
        finally
        {
            if (!durable || resultSettled) await RetireFixtureResourcesAsync(fixture, launched?.SessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The cell's whole drive budget: the task's own agent timeout plus a fixed grace margin absorbing Launch's routing/projection/dispatch overhead — this runner drives the engine itself (see <see cref="DriveToTerminalAsync"/>), so no external background worker's latency needs a separate allowance.</summary>
    private static DateTimeOffset DriveDeadline(BenchmarkTask task) => DateTimeOffset.UtcNow.AddSeconds(task.TimeoutSeconds + DriveGraceSeconds);

    private const int DriveGraceSeconds = 60;

    /// <summary>Open one FRESH child lifetime scope (its own <c>CodeSpaceDbContext</c> + connection), run <paramref name="action"/> against it, then dispose it. The one seam every DB-touching / engine-driving operation in this class goes through — see the scope-discipline note on the class itself.</summary>
    private async Task<TResult> InFreshScopeAsync<TResult>(Func<ILifetimeScope, Task<TResult>> action)
    {
        await using var scope = _scope.BeginLifetimeScope();
        return await action(scope).ConfigureAwait(false);
    }

    private async Task InFreshScopeAsync(Func<ILifetimeScope, Task> action)
    {
        await using var scope = _scope.BeginLifetimeScope();
        await action(scope).ConfigureAwait(false);
    }
}
