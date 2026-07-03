using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// The pluggable GRADING ORACLE (Rule 7 / ISP — narrow on purpose): given a finished benchmark run and the
/// workspace it left behind, decide whether the task was actually SOLVED. The instrument ships exactly one
/// implementation in slice-1 — <see cref="Graders.TestsPassGrader"/> — and resolves graders by
/// <see cref="BenchmarkGradingKind"/> through <c>IBenchmarkGraderRegistry</c>, so an LLM-judge or diff-match
/// grader lands later as a SIBLING implementation with a new kind, never by widening this contract.
///
/// <para><b>Honest by construction:</b> a grader is handed the run's WORKSPACE + a runner, not the agent — it
/// reaches an independent verdict (the tests-pass grader re-runs the repo's own tests), so the score is never the
/// model's opinion of itself.</para>
/// </summary>
public interface IBenchmarkGrader
{
    /// <summary>The grading kind this grader implements — the key the registry resolves by.</summary>
    BenchmarkGradingKind Kind { get; }

    /// <summary>
    /// Grade the run described by <paramref name="context"/> and return a pass/fail verdict with a one-line detail.
    /// MUST be independent of the agent's self-report. A grader that cannot reach a verdict (no workspace, no test
    /// command) returns a FAILED grade with a descriptive detail rather than throwing — a non-gradable run is a
    /// fail, not an instrument crash. Throws only for genuine infrastructure failures.
    /// </summary>
    Task<BenchmarkGrade> GradeAsync(BenchmarkGradingContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Everything a grader needs to reach an independent verdict on a finished run: the task being graded, the
/// post-run workspace directory (null when the run had no workspace), and the runner to execute a grading command
/// in. Kept as a record so a new grading kind that needs more (e.g. the golden patch for diff-match) extends it
/// without changing the <see cref="IBenchmarkGrader"/> signature.
/// </summary>
public sealed record BenchmarkGradingContext
{
    public required BenchmarkTask Task { get; init; }

    /// <summary>Absolute path to the workspace the agent ran in (its changes are on disk here). Null = the run had no workspace, which the tests-pass grader treats as ungradable → fail.</summary>
    public string? WorkspaceDirectory { get; init; }

    /// <summary>The sandbox runner the grader runs its grading command in — the SAME runner kind the agent ran on, so the test command executes in an environment consistent with the run.</summary>
    public required ISandboxRunner Runner { get; init; }

    /// <summary>The team the graded run belongs to — what a model-backed grader (the rubric judge) resolves its model pool with. Null on the corpus path (<see cref="ForCommand"/>), whose graders are all model-free; the judge grader fails closed without it.</summary>
    public Guid? TeamId { get; init; }

    /// <summary>The FULL acceptance spec being graded (triad S7) — kind-specific payloads beyond the command ride here (the <c>LlmJudge</c> rubric, the <c>ArtifactSchema</c> schema). Null on the corpus path; spec-requiring graders fail closed without it.</summary>
    public SupervisorAcceptanceSpec? Acceptance { get; init; }

    /// <summary>
    /// Build a grading context for an AD-HOC command grade — a caller that holds only a test command + a prepared
    /// workspace + a runner, not a corpus <see cref="BenchmarkTask"/> (e.g. the supervisor's objective acceptance
    /// gate). The corpus-only fields are pinned here ONCE, so the <see cref="Graders.TestsPassGrader"/> oracle is
    /// reused verbatim (it reads only <see cref="BenchmarkTask.TestCommand"/> + <see cref="BenchmarkTask.TimeoutSeconds"/>)
    /// without any caller hand-stubbing a fake task or duplicating the run-command→exit-code logic.
    /// </summary>
    public static BenchmarkGradingContext ForCommand(IReadOnlyList<string> command, int timeoutSeconds, string workspaceDirectory, ISandboxRunner runner) => new()
    {
        Task = new BenchmarkTask
        {
            Id = "ad-hoc-command",
            Description = "ad-hoc command grade",
            FixtureRef = "",
            Goal = "",
            Grading = BenchmarkGradingKind.TestsPass,
            Harness = "",
            Modes = Array.Empty<BenchmarkMode>(),
            TestCommand = command,
            TimeoutSeconds = timeoutSeconds,
        },
        WorkspaceDirectory = workspaceDirectory,
        Runner = runner,
    };

    /// <summary>
    /// Build a grading context for an ACCEPTANCE-SPEC grade (triad S7) — the supervisor/executor oracle gates, which
    /// hold a full <see cref="SupervisorAcceptanceSpec"/> (command + kind + rubric/schema) and a team. The spec's
    /// command rides <c>Task.TestCommand</c> exactly like <see cref="ForCommand"/>, so the command-reading graders
    /// (tests-pass, artifact-present) behave byte-identically; the spec itself + the team ride alongside for the
    /// kind-specific graders.
    /// </summary>
    public static BenchmarkGradingContext ForAcceptance(SupervisorAcceptanceSpec spec, Guid teamId, int timeoutSeconds, string workspaceDirectory, ISandboxRunner runner)
    {
        var context = ForCommand(spec.Command, timeoutSeconds, workspaceDirectory, runner);

        return context with
        {
            Task = context.Task with { Grading = spec.Kind ?? BenchmarkGradingKind.TestsPass },
            TeamId = teamId,
            Acceptance = spec,
        };
    }
}
