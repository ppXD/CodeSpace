using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// Pins that the seed corpus is genuinely RUNNABLE end-to-end, not declarative-only data: every
/// <c>SeedBenchmarkCorpus</c> task's <c>FixtureRef</c> can be materialised by <see cref="SeedBenchmarkFixtures"/>
/// into a real on-disk repo whose <c>check.sh</c> FAILS in the shipped start-state (the agent's job is to fix it),
/// and at least one fixture's documented one-line edit makes the SAME check pass. The check is run through the REAL
/// <see cref="LocalProcessRunner"/> — the same runner the grader uses — so this proves the corpus → fixture → check
/// path, the missing link the runner contract needs from its caller.
///
/// POSIX-only (Rule 12.1): the fixture scripts are /bin/sh files the runner spawns.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SeedBenchmarkFixturesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-bench-fixtures-" + Guid.NewGuid().ToString("N"));

    public SeedBenchmarkFixturesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void File_name_constants_match_the_seed_corpus_test_command()
    {
        // The corpus runs `sh check.sh`; the materialiser must write exactly that file (Rule 8 — drift here silently
        // makes every staged fixture ungradable).
        SeedBenchmarkFixtures.CheckFileName.ShouldBe("check.sh");
        SeedBenchmarkCorpus.DefaultTestCommand.ShouldBe(new[] { "sh", SeedBenchmarkFixtures.CheckFileName });
    }

    [Fact]
    public async Task Every_seed_fixture_materialises_and_its_check_fails_in_the_shipped_start_state()
    {
        if (OperatingSystem.IsWindows()) return;

        foreach (var task in SeedBenchmarkCorpus.Tasks)
        {
            var dir = StageFixture(task.FixtureRef);

            File.Exists(Path.Combine(dir, SeedBenchmarkFixtures.CheckFileName)).ShouldBeTrue($"'{task.FixtureRef}' must materialise a check.sh");
            File.Exists(Path.Combine(dir, SeedBenchmarkFixtures.SolutionFileName)).ShouldBeTrue($"'{task.FixtureRef}' must materialise the editable solution file");

            var result = await RunCheckAsync(dir);

            result.Status.ShouldBe(SandboxStatus.Failed, $"'{task.FixtureRef}' must ship in its FAILING start-state — the agent's job is to make it pass");
            result.ExitCode.ShouldNotBe(0);
        }
    }

    [Fact]
    public async Task The_documented_one_line_edit_makes_a_fixtures_check_pass()
    {
        if (OperatingSystem.IsWindows()) return;

        // Stand in for the agent on the simplest fixture: the documented fix is to set REPORTED_SUM to 5.
        var dir = StageFixture("failing-assertion");

        (await RunCheckAsync(dir)).Status.ShouldBe(SandboxStatus.Failed, "pre-edit the check fails");

        File.WriteAllText(Path.Combine(dir, SeedBenchmarkFixtures.SolutionFileName), "#!/bin/sh\nREPORTED_SUM=5\n");

        (await RunCheckAsync(dir)).Status.ShouldBe(SandboxStatus.Success, "the documented one-line edit makes the SAME check exit 0 → solved");
    }

    [Theory]
    [InlineData("fizzbuzz")]
    [InlineData("grade-boundaries")]
    [InlineData("balanced-parens")]
    [InlineData("gcd-euclid")]
    [InlineData("clamp-range")]
    public async Task A_known_correct_solution_makes_each_harder_fixtures_check_pass(string fixtureRef)
    {
        if (OperatingSystem.IsWindows()) return;

        // The well-formedness guarantee the start-state-fails test alone can't give: each harder fixture is SOLVABLE —
        // its check.sh is a CORRECT oracle (not a buggy one that never passes → an unsolvable task a live model would
        // always score 0 on, a false signal). Stand in for the agent with a known-correct solution; the SAME check.sh
        // must flip Failed → Success. (start-state-fails for all 9 tasks is covered by the loop test above.)
        var dir = StageFixture(fixtureRef);

        (await RunCheckAsync(dir)).Status.ShouldBe(SandboxStatus.Failed, $"'{fixtureRef}' must fail before the fix");

        File.WriteAllText(Path.Combine(dir, SeedBenchmarkFixtures.SolutionFileName), KnownCorrectSolution(fixtureRef));

        (await RunCheckAsync(dir)).Status.ShouldBe(SandboxStatus.Success, $"'{fixtureRef}' is solvable — a correct solution makes its multi-case check exit 0");
    }

    [Fact]
    public void An_unknown_fixture_ref_throws_rather_than_staging_an_empty_dir()
    {
        Should.Throw<ArgumentException>(() => SeedBenchmarkFixtures.Stage("not-a-fixture", Path.Combine(_root, "nope")));
    }

    // ─── Helpers ───

    /// <summary>A reference solution for each harder fixture — proves the check.sh is a CORRECT, SOLVABLE oracle. These stand in for the agent; the real model authors its own (this only certifies the task is answerable, not the only answer).</summary>
    private static string KnownCorrectSolution(string fixtureRef) => fixtureRef switch
    {
        "fizzbuzz" => """
            #!/bin/sh
            fizzbuzz() { n=$1; if [ $((n%15)) -eq 0 ]; then echo FizzBuzz; elif [ $((n%3)) -eq 0 ]; then echo Fizz; elif [ $((n%5)) -eq 0 ]; then echo Buzz; else echo "$n"; fi; }
            """,
        "grade-boundaries" => """
            #!/bin/sh
            letter_grade() { s=$1; if [ "$s" -ge 90 ]; then echo A; elif [ "$s" -ge 80 ]; then echo B; elif [ "$s" -ge 70 ]; then echo C; elif [ "$s" -ge 60 ]; then echo D; else echo F; fi; }
            """,
        "balanced-parens" => """
            #!/bin/sh
            is_balanced() { s=$1; d=0; while [ -n "$s" ]; do c=${s%"${s#?}"}; s=${s#?}; case $c in "(") d=$((d+1));; ")") d=$((d-1));; esac; if [ "$d" -lt 0 ]; then echo no; return; fi; done; if [ "$d" -eq 0 ]; then echo yes; else echo no; fi; }
            """,
        "gcd-euclid" => """
            #!/bin/sh
            gcd() { a=$1; b=$2; while [ "$b" -ne 0 ]; do t=$b; b=$((a%b)); a=$t; done; echo "$a"; }
            """,
        "clamp-range" => """
            #!/bin/sh
            clamp() { x=$1; lo=$2; hi=$3; if [ "$x" -lt "$lo" ]; then echo "$lo"; elif [ "$x" -gt "$hi" ]; then echo "$hi"; else echo "$x"; fi; }
            """,
        _ => throw new ArgumentException($"no known solution wired for '{fixtureRef}'", nameof(fixtureRef)),
    };

    private string StageFixture(string fixtureRef)
    {
        var dir = Path.Combine(_root, fixtureRef + "-" + Guid.NewGuid().ToString("N"));
        SeedBenchmarkFixtures.Stage(fixtureRef, dir);
        return dir;
    }

    private static async Task<SandboxResult> RunCheckAsync(string dir) =>
        await new LocalProcessRunner().RunAsync(new SandboxSpec
        {
            Command = "sh",
            Args = new[] { SeedBenchmarkFixtures.CheckFileName },
            WorkingDirectory = dir,
            TimeoutSeconds = 30,
        }, CancellationToken.None);
}
