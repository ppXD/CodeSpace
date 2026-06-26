namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// Which OBJECTIVE oracle decides whether a benchmark task was solved. The instrument ships ONE concrete
/// oracle for v1 — <see cref="TestsPass"/> — chosen because it is deterministic, automatable, and INDEPENDENT
/// of the agent (it runs the fixture repo's own tests; it never asks the model whether it succeeded). The
/// remaining members are DOCUMENTED follow-ons (not built in slice-1) so the seam doesn't have to widen later
/// when they land — each becomes its own <c>IBenchmarkGrader</c> (Rule 7), resolved by this kind.
/// </summary>
public enum BenchmarkGradingKind
{
    /// <summary>
    /// SWE-bench-style: after the agent's change, run the fixture repo's test command in the sandbox; exit 0 =
    /// solved. The only oracle built in slice-1. Honest because the grader runs the REPO'S tests, not the model's
    /// self-report — pass/fail is the code's, not the agent's opinion of itself.
    /// </summary>
    TestsPass,

    /// <summary>FOLLOW-ON (not built): an LLM judges the change against a rubric. Subjective; needs its own bias controls — deferred.</summary>
    LlmJudge,

    /// <summary>FOLLOW-ON (not built): the produced unified diff is matched against a known-good golden patch. Brittle to formatting; deferred.</summary>
    DiffMatch,

    /// <summary>
    /// The declared DELIVERABLE artifact(s) exist in the produced workspace — a deterministic, agent-INDEPENDENT
    /// "definition of done" for NON-coding work (research / analysis / audit) whose output is a file, not a passing
    /// test. The grading command is read as a list of repo-relative paths that MUST be present on the produced branch;
    /// all present = solved. Like <see cref="TestsPass"/> the verdict is the code's filesystem check on an independent
    /// clone, never the model's opinion of itself.
    /// </summary>
    ArtifactPresent,
}
