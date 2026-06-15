using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;

/// <summary>
/// The FIRST concrete grading oracle (SWE-bench-style): after the agent's change, run the fixture repo's OWN
/// test command in the sandbox against the post-run workspace; exit 0 = the task is solved. Deterministic,
/// automatable, and — the honesty property — INDEPENDENT of the agent: it never asks the model whether it
/// succeeded; it runs the repo's tests and reads the exit code. A run can land Succeeded yet FAIL this grade
/// (the agent finished but didn't fix the tests), which is exactly the self-report gap this oracle closes.
/// </summary>
public sealed class TestsPassGrader : IBenchmarkGrader, ISingletonDependency
{
    public BenchmarkGradingKind Kind => BenchmarkGradingKind.TestsPass;

    public async Task<BenchmarkGrade> GradeAsync(BenchmarkGradingContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.WorkspaceDirectory))
            return Fail("no-workspace");

        if (context.Task.TestCommand.Count == 0)
            return Fail("no-test-command");

        var spec = BuildGradingSpec(context);

        var result = await context.Runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);

        return result.Status == SandboxStatus.Success
            ? new BenchmarkGrade { Passed = true, Detail = "tests-passed" }
            : Fail(DetailFor(result));
    }

    /// <summary>The grading command runs in the post-run workspace with a fresh, short wall-clock cap — the tests are tiny, and a hung test is a fail, not a hang. The env is the runner's scrubbed default (no agent secret injected — the grader is independent of the agent's credential).</summary>
    private static SandboxSpec BuildGradingSpec(BenchmarkGradingContext context) => new()
    {
        Command = context.Task.TestCommand[0],
        Args = context.Task.TestCommand.Skip(1).ToList(),
        WorkingDirectory = context.WorkspaceDirectory,
        TimeoutSeconds = context.Task.TimeoutSeconds,
    };

    private static string DetailFor(SandboxResult result) =>
        result.Status == SandboxStatus.TimedOut ? "tests-timed-out" : $"tests-failed-exit-{result.ExitCode}";

    private static BenchmarkGrade Fail(string detail) => new() { Passed = false, Detail = detail };
}
