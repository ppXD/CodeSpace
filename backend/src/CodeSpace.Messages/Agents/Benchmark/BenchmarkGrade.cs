namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// The grader's verdict on one run — pass/fail plus an operator-facing detail (e.g. the failing test's exit code
/// / a one-line reason). A pure data envelope returned by <c>IBenchmarkGrader</c>; the runner folds it into a
/// <see cref="BenchmarkResult"/>. Kept separate from the result so a grader is a clean function "given the run +
/// its workspace, did it solve the task" with no knowledge of scorecards or modes.
/// </summary>
public sealed record BenchmarkGrade
{
    /// <summary>True when the oracle judged the task solved (for tests-pass: the test command exited 0 in the post-run workspace).</summary>
    public required bool Passed { get; init; }

    /// <summary>Short machine-ish reason (e.g. "tests-passed", "tests-failed-exit-1", "no-workspace", "no-test-command") — for the result row + diagnostics.</summary>
    public required string Detail { get; init; }
}
