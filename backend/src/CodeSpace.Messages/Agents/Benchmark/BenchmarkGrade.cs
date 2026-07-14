using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// WHY a grade failed, minted AT THE SOURCE by the arm that knows (P2a-3b) — the typed replacement for the
/// string-prefix detail conventions. <see cref="Genuine"/> = the oracle ran and the work failed it;
/// <see cref="GraderFault"/> = the grading machinery itself broke; <see cref="Environment"/> = clone/setup/timeout
/// infrastructure; <see cref="SpecIncomplete"/> = a half-authored contract (no rubric/schema) no retry can fix.
/// Null on a grade = an arm not yet minting (or a pre-P2a-3b tape) — consumers fall back to the pinned detail
/// conventions, which retire as the remaining arms and the tape learn the type.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GradeFailureClass
{
    Genuine,
    GraderFault,
    Environment,
    SpecIncomplete,
}

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

    /// <summary>Short machine-ish reason (e.g. "tests-passed", "tests-failed-exit-1", "no-workspace", "no-test-command") — for the result row + diagnostics. DISPLAY-first since <see cref="Class"/>: classification prefers the type.</summary>
    public required string Detail { get; init; }

    /// <summary>The typed failure class, minted at the source arm. Null → the arm predates typing; consumers fall back to the detail conventions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GradeFailureClass? Class { get; init; }
}
