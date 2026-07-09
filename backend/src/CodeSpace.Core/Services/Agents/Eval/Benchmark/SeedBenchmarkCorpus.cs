using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// The small, fixed SEED corpus the instrument ships with — 9 tasks across two difficulty tiers, every one gradable
/// by the tests-pass oracle. An EASY tier (4 one-edit tasks — the slice-1 plumbing floor) plus a HARDER tier (5 tasks
/// needing real reasoning + a known-correct multi-case answer: implement-from-spec, multi-bug boundary fix, a stack
/// algorithm, Euclid's gcd, edge-case bounding). The harder tier exists so a LIVE model's solve-rate DIFFERENTIATES —
/// a one-edit-only corpus a capable model trivially clears at ~100% measures nothing.
///
/// <para>Each task's <see cref="BenchmarkTask.FixtureRef"/> names a fixture that <see cref="SeedBenchmarkFixtures.Stage"/>
/// materialises as a self-contained, OFFLINE local repo: a directory with a check script that exits non-zero until the
/// agent fixes the editable solution file, re-run by the <see cref="BenchmarkTask.TestCommand"/>. Every harder task's
/// <see cref="BenchmarkTask.Goal"/> is behaviour-only (it never names the fix) so the model must read + understand +
/// write the logic itself. The fixtures need no network, no model key, no package manager — so CI runs them through the
/// fake CLI to prove the PLUMBING, and a real-model run on demand produces the real comparison numbers.</para>
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

        // ── Harder tier: real reasoning + a multi-case check, so a live model's solve-rate DIFFERENTIATES rather than
        //    trivially hitting 100% on the one-edit easy tier above. Same offline tests-pass shape; the goal stays
        //    behaviour-only (never names the fix) so the model must read + understand + write the logic itself.

        Task(
            id: "implement-fizzbuzz",
            description: "A function must echo Fizz/Buzz/FizzBuzz by divisibility but only echoes its input; implement the full rule.",
            fixtureRef: "fizzbuzz",
            goal: "The check script exercises a function over several inputs and currently fails. Run it, read what it expects, and implement the function so every case passes."),

        Task(
            id: "fix-grade-boundaries",
            description: "A score-to-letter function is wrong exactly at the grade boundaries; fix the comparisons so the boundary scores grade correctly.",
            fixtureRef: "grade-boundaries",
            goal: "The check script fails because a function returns the wrong result at certain boundary inputs. Run it, find why the boundaries are wrong, and fix the code so every case passes."),

        Task(
            id: "implement-balanced-parens",
            description: "A bracket-matching function always reports success; implement the real in-order balance check so unbalanced inputs are rejected.",
            fixtureRef: "balanced-parens",
            goal: "The check script feeds several inputs to a function and currently fails. Run it, work out the rule it expects, and implement the function so every case passes."),

        Task(
            id: "implement-gcd-euclid",
            description: "A greatest-common-divisor function returns a placeholder; implement the algorithm so it computes the real value.",
            fixtureRef: "gcd-euclid",
            goal: "The check script expects a function to compute a value over several inputs but it returns a placeholder. Implement it so every case passes."),

        Task(
            id: "implement-clamp-range",
            description: "A range-bounding function returns its input unbounded; implement bounding (including a negative range) so every case passes.",
            fixtureRef: "clamp-range",
            goal: "The check script feeds a function values below, above, and inside a range and currently fails. Run it, infer the expected behaviour, and implement the function so every case passes."),
    };

    /// <summary>
    /// P4.2 — the EXTENDED tier: meaningfully harder multi-step algorithms (not the base tier's single comparison /
    /// loop-bound / depth-counter), so a real model likely needs several self-correction rounds rather than one
    /// read-and-fix pass — exercising the TimedOut path honestly instead of every pair clearing in seconds.
    /// Deliberately kept OUT of <see cref="Tasks"/> (the default corpus every push-triggered CI run drives): these
    /// run through their OWN opt-in real-model CI lane (workflow_dispatch only) so a harder, possibly-slower pair can
    /// never put the existing 60-minute gating floor's budget at risk. A generous <see cref="BenchmarkTask.TimeoutSeconds"/>
    /// (20 minutes) accommodates genuine multi-round self-correction without artificially cutting it short.
    /// </summary>
    public static IReadOnlyList<BenchmarkTask> ExtendedTasks { get; } = new[]
    {
        Task(
            id: "implement-roman-numeral",
            description: "Convert an integer (1-3999) to a Roman numeral using standard subtractive notation; the stub returns the number unconverted.",
            fixtureRef: "roman-numeral",
            goal: "The check script exercises a function that should convert a number to its Roman numeral form (standard subtractive notation, e.g. 4 is IV, 1994 is MCMXCIV) but it currently returns the number unconverted. Run the check, work out the full rule from the cases it exercises, and implement the function so every case passes.",
            timeoutSeconds: ExtendedTimeoutSeconds),

        Task(
            id: "implement-expr-precedence",
            description: "Evaluate a space-separated arithmetic expression respecting operator precedence (x and / before + and -); the stub evaluates strictly left to right.",
            fixtureRef: "expr-precedence",
            goal: "The check script exercises a function that should evaluate a simple arithmetic expression (space-separated numbers and operators) but it currently evaluates strictly left to right with no regard for operator precedence, so several cases fail. Run the check, work out which operators must bind tighter than others, and implement the function so every case passes.",
            timeoutSeconds: ExtendedTimeoutSeconds),
    };

    /// <summary>The extended tier's generous wall-clock cap (20 minutes) — a genuinely harder multi-step algorithm may need several self-correction rounds; this is a ceiling to accommodate that, not a target duration.</summary>
    internal const int ExtendedTimeoutSeconds = 1200;

    private static BenchmarkTask Task(string id, string description, string fixtureRef, string goal, int? timeoutSeconds = null)
    {
        var task = new BenchmarkTask
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

        return timeoutSeconds is { } seconds ? task with { TimeoutSeconds = seconds } : task;
    }
}
