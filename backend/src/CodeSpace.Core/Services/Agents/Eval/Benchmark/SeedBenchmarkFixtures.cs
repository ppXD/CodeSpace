namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Materialises a <see cref="SeedBenchmarkCorpus"/> task's <see cref="Messages.Agents.Benchmark.BenchmarkTask.FixtureRef"/>
/// into a self-contained, OFFLINE local repo on disk — the "stage a fresh copy per (task, mode)" step the
/// <c>IBenchmarkRunner</c> contract requires of its caller. Each fixture is a directory with two POSIX-shell files:
/// a <c>solution.sh</c> (the "code" the agent edits, shipped in its FAILING start-state) and the seed corpus's
/// <c>check.sh</c> oracle (which sources <c>solution.sh</c> and exits non-zero until the documented one-line edit is
/// made). No network, no package manager, no model key — so CI runs them through the fake CLI to prove the plumbing,
/// and the tests-pass grader re-runs the SAME <c>check.sh</c> afterwards (Rule 7 honesty: the grade is the repo's
/// check, never the model's self-report).
///
/// <para>The materialiser writes ONLY the failing start-state; making each check exit 0 is the agent's job (or, in a
/// plumbing test, a manual edit). A fixture is intentionally tiny + uniform — a single sourced variable / loop bound /
/// guard the documented goal points at — so the corpus is genuinely runnable end-to-end, not declarative-only data.</para>
/// </summary>
public static class SeedBenchmarkFixtures
{
    /// <summary>The fixture file the agent edits to solve the task. The check sources it; shipped in its failing start-state.</summary>
    public const string SolutionFileName = "solution.sh";

    /// <summary>The oracle the seed corpus runs (matches <see cref="SeedBenchmarkCorpus.DefaultTestCommand"/>). Sources <see cref="SolutionFileName"/> and exits 0 only once the documented edit is made.</summary>
    public const string CheckFileName = "check.sh";

    /// <summary>
    /// Stage the fixture named by <paramref name="fixtureRef"/> into <paramref name="directory"/> in its FAILING
    /// start-state (the directory is created if absent). Throws for an unknown ref so a typo in the corpus surfaces
    /// loudly rather than staging an empty dir the grader would silently fail.
    /// </summary>
    public static void Stage(string fixtureRef, string directory)
    {
        var fixture = Resolve(fixtureRef);

        Directory.CreateDirectory(directory);

        WriteScript(Path.Combine(directory, SolutionFileName), fixture.Solution);
        WriteScript(Path.Combine(directory, CheckFileName), fixture.Check);
    }

    /// <summary>The fixture's two scripts: the editable solution (failing start-state) + the check oracle that grades it.</summary>
    private static (string Solution, string Check) Resolve(string fixtureRef) => fixtureRef switch
    {
        // A hardcoded sum that is off by one; the agent fixes the operand so the check's recomputed total matches.
        "failing-assertion" => (
            Solution: "#!/bin/sh\n# The reported sum is wrong by one. Fix the operand so it equals 5.\nREPORTED_SUM=4\n",
            Check: "#!/bin/sh\n. ./solution.sh\n[ \"$REPORTED_SUM\" = \"5\" ]\n"),

        // A function returns a placeholder; the agent implements it to return the correct value.
        "missing-function" => (
            Solution: "#!/bin/sh\n# double() must return 2*n. It returns a placeholder. Implement it.\ndouble() { echo 0; }\n",
            Check: "#!/bin/sh\n. ./solution.sh\n[ \"$(double 21)\" = \"42\" ]\n"),

        // A loop bound is off by one so the summed total is wrong; the agent corrects the bound.
        "off-by-one-loop" => (
            Solution: "#!/bin/sh\n# Sum 1..N. The bound is off by one (stops at 4, should reach 5).\nsum_to_five() { t=0; i=1; while [ \"$i\" -le 4 ]; do t=$((t+i)); i=$((i+1)); done; echo \"$t\"; }\n",
            Check: "#!/bin/sh\n. ./solution.sh\n[ \"$(sum_to_five)\" = \"15\" ]\n"),

        // Empty input currently crashes / returns nothing; the agent adds a guard returning the expected default.
        "missing-guard" => (
            Solution: "#!/bin/sh\n# first_or_default should echo 'none' for empty input. It echoes the input unguarded.\nfirst_or_default() { echo \"$1\"; }\n",
            Check: "#!/bin/sh\n. ./solution.sh\n[ \"$(first_or_default '')\" = \"none\" ]\n"),

        _ => throw new ArgumentException($"Unknown benchmark fixture '{fixtureRef}'. Known: failing-assertion, missing-function, off-by-one-loop, missing-guard.", nameof(fixtureRef)),
    };

    /// <summary>Write a POSIX script 0755 (owner rwx, group/other r-x) so the runner can spawn / source it. A no-op on file mode where it doesn't apply.</summary>
    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
