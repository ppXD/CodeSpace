using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Drives one (task, mode) through the REAL agent-execution pipeline, grades it with the task's objective oracle,
/// and records a <see cref="BenchmarkResult"/>. Thin orchestration over the same production seams the agent.code
/// node uses (Rule 16): <c>IAgentRunService.CreateAsync</c> → <c>IAgentRunExecutor.ExecuteAsync</c> →
/// <c>IBenchmarkGrader.GradeAsync</c>. The runner adds NO new execution path — it reuses the executor, the runner
/// registry, and the grader registry; the model behind the CLI is the environment's (a fake CLI in CI).
///
/// <para><b>Mode handling.</b> <see cref="BenchmarkMode.HarnessCli"/> and <see cref="BenchmarkMode.HarnessCliWithMcp"/>
/// run identically through this runner — they differ only by whether the deployment-level MCP-endpoint flag
/// (<c>CODESPACE_AGENT_MCP_ENDPOINT_ENABLED</c>) is on, which the operator sets for the run batch (a process-wide
/// flag a service must not flip mid-flight under concurrency); the runner records the distinguishing mode LABEL on
/// the result either way. <see cref="BenchmarkMode.WorkflowMap"/> is driven through the composed
/// planner→<c>flow.map</c>→synthesizer ENGINE path, which needs a seeded workflow + the engine — so it is exercised
/// by the benchmark integration harness (reusing the D2 headline-flow path), NOT this single-run service; calling it
/// here throws so the boundary is explicit, never a silent fake.</para>
/// </summary>
public sealed class BenchmarkRunner : IBenchmarkRunner, IScopedDependency
{
    private readonly IAgentRunService _runs;
    private readonly IAgentRunExecutor _executor;
    private readonly Sandbox.ISandboxRunnerRegistry _runners;
    private readonly IBenchmarkGraderRegistry _graders;

    /// <summary>Runner used when the task pins none — the in-process local runner, matching the executor's own default.</summary>
    private const string DefaultRunnerKind = "local";

    public BenchmarkRunner(IAgentRunService runs, IAgentRunExecutor executor, Sandbox.ISandboxRunnerRegistry runners, IBenchmarkGraderRegistry graders)
    {
        _runs = runs;
        _executor = executor;
        _runners = runners;
        _graders = graders;
    }

    public async Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, string workspaceDirectory, Guid teamId, CancellationToken cancellationToken)
    {
        if (mode == BenchmarkMode.WorkflowMap)
            throw new NotSupportedException("BenchmarkMode.WorkflowMap is driven through the composed planner→flow.map→synthesizer engine path by the benchmark integration harness, not this single-run runner. See the benchmark integration test that reuses the D2 headline-flow path.");

        var run = await CreateRunAsync(task, workspaceDirectory, teamId, cancellationToken).ConfigureAwait(false);

        await _executor.ExecuteAsync(run.Id, cancellationToken).ConfigureAwait(false);

        var completed = await _runs.GetAsync(run.Id, cancellationToken).ConfigureAwait(false);

        var grade = await GradeAsync(task, workspaceDirectory, cancellationToken).ConfigureAwait(false);

        return BuildResult(task, mode, completed, grade);
    }

    /// <summary>Create the agent run for this task with the pre-staged workspace pinned directly (no RepositoryId → the executor uses the task's WorkspaceDirectory as the sandbox cwd). The autonomy stays Standard so the agent may write inside its workspace.</summary>
    private async Task<AgentRun> CreateRunAsync(BenchmarkTask task, string workspaceDirectory, Guid teamId, CancellationToken cancellationToken)
    {
        var agentTask = new AgentTask
        {
            Goal = task.Goal,
            Harness = task.Harness,
            RunnerKind = DefaultRunnerKind,
            WorkspaceDirectory = workspaceDirectory,
            Autonomy = AgentAutonomyLevel.Standard,
            TimeoutSeconds = task.TimeoutSeconds,
        };

        return await _runs.CreateAsync(agentTask, teamId, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Grade the finished run with the task's oracle, against the post-run workspace, on the same runner kind the agent ran on. The grader is independent of the agent (it re-runs the repo's tests).</summary>
    private async Task<BenchmarkGrade> GradeAsync(BenchmarkTask task, string workspaceDirectory, CancellationToken cancellationToken)
    {
        var grader = _graders.Resolve(task.Grading);

        var context = new BenchmarkGradingContext
        {
            Task = task,
            WorkspaceDirectory = workspaceDirectory,
            Runner = _runners.Resolve(DefaultRunnerKind),
        };

        return await grader.GradeAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fold the recorded run + the grade into a result row. Duration is the run's wall-clock when both timestamps exist (mirroring the scorecard's own projection); null otherwise.</summary>
    private static BenchmarkResult BuildResult(BenchmarkTask task, BenchmarkMode mode, AgentRun run, BenchmarkGrade grade) => new()
    {
        TaskId = task.Id,
        Mode = mode,
        AgentRunId = run.Id,
        RunStatus = run.Status,
        DurationSeconds = run.StartedAt is { } started && run.CompletedAt is { } completed ? (completed - started).TotalSeconds : null,
        Grade = grade,
        PlanRanCleanWithNoHumanEdits = null,   // only meaningful for WorkflowMap (driven by the integration harness); PR-D wires the no-human-edits signal.
    };
}
