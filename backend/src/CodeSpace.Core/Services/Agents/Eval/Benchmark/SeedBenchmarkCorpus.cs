using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// The small, fixed SEED corpus the instrument ships with — 4 tasks, every one gradable by the tests-pass
/// oracle. Deliberately TINY (Rule 2 / the slice-1 scope guard): this is enough to prove the instrument
/// end-to-end — task → mode → grade → scorecard row — NOT a 20-task real-model suite (an explicit follow-on).
///
/// <para>Each task's <see cref="BenchmarkTask.FixtureRef"/> names a fixture that <see cref="SeedBenchmarkFixtures.Stage"/>
/// materialises as a self-contained, OFFLINE local repo: a directory with a check script that exits non-zero until the
/// agent makes the documented one-line edit, plus the editable solution file, re-run by the
/// <see cref="BenchmarkTask.TestCommand"/>. The fixtures need no network, no model key, no package manager — so CI runs
/// them through the fake CLI to prove the PLUMBING, and a real-model run on demand produces the real comparison numbers.</para>
/// </summary>
public static class SeedBenchmarkCorpus
{
    /// <summary>
    /// The default modes every seed task is compared across — the two the single-run <c>BenchmarkRunner</c> can drive
    /// end-to-end today. <see cref="BenchmarkMode.WorkflowMap"/> is deliberately NOT here: it needs the composed
    /// planner→<c>flow.map</c>→synthesizer engine (a workflow, not a single agent run), which this slice does not wire,
    /// so listing it would make the shipped corpus throw on every task. It lands when the workflow-driving harness does.
    /// </summary>
    public static readonly IReadOnlyList<BenchmarkMode> DefaultModes = new[]
    {
        BenchmarkMode.HarnessCli,
        BenchmarkMode.HarnessCliWithMcp,
    };

    /// <summary>The default test command for every seed fixture — run the fixture's own POSIX check script. Independent of the agent (Rule 7 honesty): the grader re-runs THIS, never the model's self-report.</summary>
    public static readonly IReadOnlyList<string> DefaultTestCommand = new[] { "sh", "check.sh" };

    /// <summary>The seed tasks. Stable order + ids so a recorded comparison is reproducible.</summary>
    public static IReadOnlyList<BenchmarkTask> Tasks { get; } = new[]
    {
        Task(
            id: "fix-failing-assertion",
            description: "A unit check asserts a hardcoded sum that is off by one; fix the operand.",
            fixtureRef: "failing-assertion",
            goal: "The check script in this repo fails. Run it, find why, and make the one-line code change so it exits 0."),

        Task(
            id: "implement-missing-function",
            description: "A check calls a function that returns a placeholder; implement it to satisfy the check.",
            fixtureRef: "missing-function",
            goal: "The check script expects a function to return the correct value but it returns a placeholder. Implement it so the check passes."),

        Task(
            id: "fix-off-by-one-loop",
            description: "A loop bound is off by one so the check's expected total is wrong; correct the bound.",
            fixtureRef: "off-by-one-loop",
            goal: "The check script reports the wrong total because a loop bound is off by one. Fix the bound so the check passes."),

        Task(
            id: "add-missing-guard",
            description: "A check feeds an empty input that currently crashes; add the guard so it returns the expected value.",
            fixtureRef: "missing-guard",
            goal: "The check script crashes on empty input. Add a guard so it returns the expected value for the empty case and passes."),
    };

    private static BenchmarkTask Task(string id, string description, string fixtureRef, string goal) => new()
    {
        Id = id,
        Description = description,
        FixtureRef = fixtureRef,
        Goal = goal,
        Grading = BenchmarkGradingKind.TestsPass,
        TestCommand = DefaultTestCommand,
        Harness = "codex-cli",
        Modes = DefaultModes,
    };
}
