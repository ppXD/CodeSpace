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
///
/// <para>The command runs under <see cref="OracleRuntime"/>: an environment the graded tree cannot extend, platform-owned
/// Python behind <c>python -I</c>, and a note on the grade (<see cref="BenchmarkGrade.OracleNote"/>) whenever what ran could
/// not be isolated — including a module the launcher resolved away from the graded tree (<see cref="OracleRuntime.ShadowNote"/>).
/// The paths the caller restored from platform-owned bytes arrive as <see cref="BenchmarkGradingContext.PinnedOraclePaths"/>.
/// The note also leads the evidence, so a reader that keeps only the evidence still sees it; the evidence still names the
/// command exactly as authored.</para>
/// </summary>
public sealed class TestsPassGrader : IBenchmarkGrader, ISingletonDependency
{
    /// <summary>
    /// The detail of a check the runner killed at its own memory ceiling (<see cref="SandboxStatus.ResourceExhausted"/>,
    /// read from the cgroup's oom_kill counter, never guessed from the exit code). A grade now runs under the producing
    /// run's ceilings, so a ceiling can end a check, and that says nothing about whether the code is right: it is an
    /// <see cref="GradeFailureClass.Environment"/> fact like <c>tests-timed-out</c>. Pinned by a unit test (Rule 8): the
    /// infra classifier and the agent.code retry verdict read this literal across a durable resume payload.
    /// </summary>
    public const string ResourceExhaustedDetail = "tests-resource-exhausted";

    public BenchmarkGradingKind Kind => BenchmarkGradingKind.TestsPass;

    public async Task<BenchmarkGrade> GradeAsync(BenchmarkGradingContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.WorkspaceDirectory))
            return Fail("no-workspace");

        if (context.Task.TestCommand.Count == 0)
            return Fail("no-test-command");

        var command = OracleRuntime.Build(context.Task.TestCommand, context.WorkspaceDirectory, context.PinnedOraclePaths, Environment.GetEnvironmentVariable("PATH"));

        var result = await context.Runner.RunAsync(BuildGradingSpec(context, command), cancellationToken).ConfigureAwait(false);

        var note = OracleRuntime.CombineNotes(command.Note, command.ReportsShadowing ? OracleRuntime.ShadowNote(result.Stderr) : null);

        var evidence = EvidenceTextFor(context, result, note);

        var grade = result.Status == SandboxStatus.Success
            ? new BenchmarkGrade { Passed = true, Detail = "tests-passed", EvidenceText = evidence }
            : Fail(DetailFor(result), result.Status is SandboxStatus.TimedOut or SandboxStatus.ResourceExhausted ? GradeFailureClass.Environment : GradeFailureClass.Genuine) with { EvidenceText = evidence };

        return grade with { OracleNote = note };
    }

    /// <summary>The grading command runs in the post-run workspace with a fresh, short wall-clock cap — the tests are tiny, and a hung test is a fail, not a hang. The env is the runner's scrubbed default plus the <see cref="OracleRuntime"/> isolation (no agent secret injected — the grader is independent of the agent's credential), and the argv is the one that runtime resolved.</summary>
    private static SandboxSpec BuildGradingSpec(BenchmarkGradingContext context, OracleRuntime.OracleCommand command) => new()
    {
        Command = command.Command,
        Args = command.Args,
        Environment = command.Environment,
        WorkingDirectory = context.WorkspaceDirectory,
        TimeoutSeconds = context.Task.TimeoutSeconds,
    };

    /// <summary>P3a-1: the oracle run's bounded evidence — the oracle note when there is one, then command, exit, and output TAILS (the failure lives at the end; a full log offloads elsewhere). Both verdict directions carry it: a PASS without evidence is as unauditable as a fail.</summary>
    private static string EvidenceTextFor(BenchmarkGradingContext context, SandboxResult result, string? note) =>
        $"{(note is null ? "" : note + "\n")}$ {string.Join(' ', context.Task.TestCommand)}\nexit={result.ExitCode} status={result.Status}\n--- stdout (tail) ---\n{Tail(result.Stdout)}\n--- stderr (tail) ---\n{Tail(result.Stderr)}";

    private static string Tail(string text) => text.Length <= 8_192 ? text : text[^8_192..];

    private static string DetailFor(SandboxResult result) => result.Status switch
    {
        SandboxStatus.TimedOut => "tests-timed-out",
        SandboxStatus.ResourceExhausted => ResourceExhaustedDetail,
        _ => $"tests-failed-exit-{result.ExitCode}",
    };

    private static BenchmarkGrade Fail(string detail, GradeFailureClass? failureClass = null) => new() { Passed = false, Detail = detail, Class = failureClass };
}
