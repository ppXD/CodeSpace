using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Resolves an <see cref="IBenchmarkGrader"/> by its <see cref="BenchmarkGradingKind"/> — the same registry
/// shape as <c>ISandboxRunnerRegistry</c> / <c>IAgentHarnessRegistry</c>. The policy that picks WHICH oracle a
/// task uses lives on the task (<see cref="BenchmarkTask.Grading"/>); this only maps a kind to its
/// implementation, so an LLM-judge / diff-match grader becomes resolvable by registering its class — no edit here.
/// </summary>
public interface IBenchmarkGraderRegistry
{
    /// <summary>Resolve the grader for <paramref name="kind"/>. Throws when none is registered (e.g. a follow-on kind whose grader isn't built yet).</summary>
    IBenchmarkGrader Resolve(BenchmarkGradingKind kind);
}
