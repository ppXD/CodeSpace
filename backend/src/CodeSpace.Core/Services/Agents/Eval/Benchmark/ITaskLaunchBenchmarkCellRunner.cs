using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// P19: the Launch-mode sibling of the single-run instrument — drives ONE (task, TaskLaunch arm) cell through the
/// REAL production Launch entry (<c>ITaskLaunchService</c>: seed → route → project → run) instead of a
/// directly-created <c>AgentRun</c>, drives the resulting workflow run to a terminal state itself (the same
/// engine + executor + resume seams a background worker would otherwise use, called inline so the cell needs no
/// background worker present), reconstructs the produced diff onto the task's pre-staged fixture directory, and
/// grades it with the SAME objective oracle the direct instrument uses. <c>BenchmarkRunner</c> delegates every
/// <see cref="BenchmarkModeEffort.IsTaskLaunch"/> mode here; it never builds an <c>AgentTask</c> or calls
/// <c>IAgentRunService</c> itself for those modes.
/// </summary>
public interface ITaskLaunchBenchmarkCellRunner
{
    Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, BenchmarkExecutionContext context, CancellationToken cancellationToken);
}
