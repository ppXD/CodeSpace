namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// One fixed, repeatable benchmark task — the unit the instrument runs through each <see cref="BenchmarkMode"/>
/// and grades. A pure data envelope (a noun, so it lives in Messages, Rule 18.1): it pins WHICH repo fixture to
/// stage, the natural-language GOAL the agent is given, the OBJECTIVE oracle that decides pass/fail, and the set
/// of modes to compare. The corpus is a list of these; running task × mode and laying the grades side by side on
/// the scorecard is what turns "mode X is better" into a number.
///
/// <para>v1 fixtures are SELF-CONTAINED, LOCAL, OFFLINE (no network, no model key needed in CI): a fixture is a
/// directory staged into the workspace; the agent's job is to make <see cref="TestCommand"/> exit 0; the
/// <see cref="BenchmarkGradingKind.TestsPass"/> grader re-runs that command afterwards. <c>SeedBenchmarkFixtures</c>
/// materialises each seed fixture as a tiny shell-scriptable repo with a failing check the agent must fix.</para>
/// </summary>
public sealed record BenchmarkTask
{
    /// <summary>Stable, human-legible id (e.g. "fix-failing-assertion"). The row key on the scorecard + in result records; must be unique within a corpus.</summary>
    public required string Id { get; init; }

    /// <summary>One-line description of what the task exercises — for the operator reading a result table.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Reference to the repo fixture this task stages. v1 = the NAME of a seed fixture <c>SeedBenchmarkFixtures.Stage</c>
    /// materialises locally (a folder of files). A future remote corpus carries a clone URL + ref here behind the same
    /// field — the runner only ever hands it to the fixture stager, never interprets it.
    /// </summary>
    public required string FixtureRef { get; init; }

    /// <summary>The natural-language goal handed to the agent (becomes the <see cref="AgentTask.Goal"/>). States the outcome, never a CLI flag.</summary>
    public required string Goal { get; init; }

    /// <summary>The oracle that grades this task. v1 = always <see cref="BenchmarkGradingKind.TestsPass"/>.</summary>
    public required BenchmarkGradingKind Grading { get; init; }

    /// <summary>
    /// The command (argv-style, never shell-split) the <see cref="BenchmarkGradingKind.TestsPass"/> grader runs in
    /// the post-run workspace to decide pass/fail — exit 0 = solved. Element 0 is the executable; the rest are args.
    /// Kept as the fixture's OWN test command so the grader stays independent of the agent. Empty for non-tests-pass
    /// gradings (a later slice).
    /// </summary>
    public IReadOnlyList<string> TestCommand { get; init; } = Array.Empty<string>();

    /// <summary>The harness CLI the agent modes (<see cref="BenchmarkMode.HarnessCli"/> / <see cref="BenchmarkMode.HarnessCliWithMcp"/>) run with — e.g. "codex-cli".</summary>
    public required string Harness { get; init; }

    /// <summary>The modes to run this task through. Each (task, mode) pair produces one <see cref="BenchmarkResult"/>.</summary>
    public required IReadOnlyList<BenchmarkMode> Modes { get; init; }

    /// <summary>Wall-clock cap for the agent run on this task (seconds). Short by default — fixtures are tiny.</summary>
    public int TimeoutSeconds { get; init; } = 120;
}
