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

    /// <summary>
    /// An LLM judges the declared deliverable file(s) against a per-instance RUBRIC (triad S7): weighted criteria,
    /// each answered with a BINARY met/not-met + evidence (never a Likert scale), aggregated as a weighted fraction
    /// vs the rubric's threshold. The grading command is the list of repo-relative deliverable paths to read and
    /// judge; the rubric rides <c>SupervisorAcceptanceSpec.Rubric</c>. The subjective step is CONTAINED: an
    /// independent judge model answers narrow per-criterion questions with evidence (auditable + meta-evaluable),
    /// and a judge that cannot produce a valid verdict fails CLOSED as a grade-error — never a silent pass.
    /// </summary>
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

    /// <summary>
    /// Every citation in the declared deliverable file(s) RESOLVES (triad S7) — a deterministic oracle for research
    /// output: each graded file must contain at least one markdown citation, every relative-path citation must
    /// resolve to a real file inside the produced workspace (clamped — no escapes), and every URL citation must be a
    /// well-formed absolute http(s) link. The grading command is the list of repo-relative deliverable paths to
    /// check. Deliberately network-free (no live fetch — the grader must stay deterministic and egress-independent);
    /// link LIVENESS is a judge/critic concern, existence and well-formedness are the code's.
    /// </summary>
    CitationsResolve,

    /// <summary>
    /// The declared deliverable file(s) parse as JSON and validate against the acceptance's JSON schema (triad S7)
    /// — the deterministic oracle for STRUCTURED output (a config, a dataset, an extraction). The grading command is
    /// the list of repo-relative paths; the schema rides <c>SupervisorAcceptanceSpec.Schema</c>. Validation is the
    /// shared focused checker (required / type / enum / nested properties+items — not full conformance), which
    /// catches the real failures (missing field, wrong shape, invalid enum) without a dependency.
    /// </summary>
    ArtifactSchema,
}
