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
/// are GENUINELY differentiated within a SINGLE process: the runner stamps the per-run opt-in
/// <c>AgentTask.EnableMcpEndpoint</c> from the mode, so the cli-mcp run opens the run-scoped MCP tool-fabric endpoint
/// while the cli run does not — they execute observably differently, not merely under different scorecard labels. The
/// resolved gate state (the SAME <c>AgentRunExecutor.ShouldOpenMcpEndpoint</c> the executor consults) is recorded on
/// <see cref="BenchmarkResult.McpEndpointEnabled"/>, so a row can never be mislabeled relative to what the executor did.
/// <see cref="BenchmarkMode.WorkflowMap"/> is RESERVED, not wired in this slice: it would run through the composed
/// planner→<c>flow.map</c>→synthesizer ENGINE path (a workflow, not a single agent run), which this single-run runner
/// does not orchestrate — requesting it throws a clear "not yet wired" so the boundary is explicit, never a silent fake.
/// The seed corpus therefore ships only the two runnable modes (see <c>SeedBenchmarkCorpus.DefaultModes</c>).</para>
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
            throw new NotSupportedException("BenchmarkMode.WorkflowMap is reserved and not yet wired: it runs through the composed planner→flow.map→synthesizer ENGINE path (a workflow, not a single agent run), which this single-run runner does not orchestrate. Run the two harness-CLI modes here; the seed corpus ships only those.");

        var agentTask = BuildAgentTask(task, mode, workspaceDirectory);

        // The SAME gate the executor will consult to decide whether to open the run's MCP endpoint — recorded on the
        // result so the cli vs cli-mcp rows can never be mislabeled relative to what the run actually did.
        var mcpEnabled = AgentRunExecutor.ShouldOpenMcpEndpoint(agentTask);

        var run = await _runs.CreateAsync(agentTask, teamId, null, null, iterationKey: "", cancellationToken).ConfigureAwait(false);

        await _executor.ExecuteAsync(run.Id, cancellationToken).ConfigureAwait(false);

        var completed = await _runs.GetAsync(run.Id, cancellationToken).ConfigureAwait(false);

        var grade = await GradeAsync(task, workspaceDirectory, cancellationToken).ConfigureAwait(false);

        return BuildResult(task, mode, completed, grade, mcpEnabled);
    }

    /// <summary>
    /// Build the agent-task envelope for this (task, mode): the pre-staged workspace is pinned directly (no RepositoryId →
    /// the executor uses WorkspaceDirectory as the sandbox cwd), autonomy stays Standard so the agent may write inside its
    /// workspace, and the per-run MCP opt-in is set IFF the mode is <see cref="BenchmarkMode.HarnessCliWithMcp"/> — the one
    /// knob that genuinely differentiates the two harness-CLI modes within a single process.
    /// </summary>
    private static AgentTask BuildAgentTask(BenchmarkTask task, BenchmarkMode mode, string workspaceDirectory) => new()
    {
        Goal = task.Goal,
        Harness = task.Harness,
        RunnerKind = DefaultRunnerKind,
        WorkspaceDirectory = workspaceDirectory,
        Autonomy = AgentAutonomyLevel.Standard,
        TimeoutSeconds = task.TimeoutSeconds,
        EnableMcpEndpoint = mode == BenchmarkMode.HarnessCliWithMcp ? true : null,
    };

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

    /// <summary>Fold the recorded run + the grade into a result row. Duration is the run's wall-clock when both timestamps exist (mirroring the scorecard's own projection); null otherwise. <paramref name="mcpEnabled"/> is the executor's resolved MCP gate for this run — the observable cli vs cli-mcp distinction.</summary>
    private static BenchmarkResult BuildResult(BenchmarkTask task, BenchmarkMode mode, AgentRun run, BenchmarkGrade grade, bool mcpEnabled) => new()
    {
        TaskId = task.Id,
        Mode = mode,
        AgentRunId = run.Id,
        RunStatus = run.Status,
        DurationSeconds = run.StartedAt is { } started && run.CompletedAt is { } completed ? (completed - started).TotalSeconds : null,
        Grade = grade,
        McpEndpointEnabled = mcpEnabled,
        PlanRanCleanWithNoHumanEdits = null,   // only meaningful for WorkflowMap (reserved, not wired in this slice); PR-D wires the no-human-edits signal.
    };
}
