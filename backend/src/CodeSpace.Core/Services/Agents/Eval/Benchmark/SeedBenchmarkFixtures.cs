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

        // ── Harder tier (slice 3): each needs real reasoning + a known-correct multi-case answer, not a one-char edit,
        //    so a live model's solve-rate DIFFERENTIATES instead of trivially hitting 100%. Every check.sh runs several
        //    cases so a naive/partial fix fails. All stay pure-POSIX + offline (no runtime/key) like the easy tier.

        // Implement-from-spec: fizzbuzz needs the modulo rule AND the FizzBuzz-before-Fizz/Buzz ordering; the stub echoes N.
        "fizzbuzz" => (
            Solution: """
                #!/bin/sh
                # fizzbuzz N: echo "Fizz" if N%3==0, "Buzz" if N%5==0, "FizzBuzz" if BOTH, else N.
                # The stub just echoes N. Implement the full rule (mind the both-divisible case).
                fizzbuzz() { echo "$1"; }
                """,
            Check: """
                #!/bin/sh
                . ./solution.sh
                [ "$(fizzbuzz 1)" = "1" ] || exit 1
                [ "$(fizzbuzz 3)" = "Fizz" ] || exit 1
                [ "$(fizzbuzz 5)" = "Buzz" ] || exit 1
                [ "$(fizzbuzz 15)" = "FizzBuzz" ] || exit 1
                [ "$(fizzbuzz 7)" = "7" ] || exit 1
                [ "$(fizzbuzz 30)" = "FizzBuzz" ] || exit 1
                [ "$(fizzbuzz 9)" = "Fizz" ] || exit 1
                [ "$(fizzbuzz 10)" = "Buzz" ] || exit 1
                """),

        // Multi-bug boundary fix: the A/B/C cutoffs are wrong exactly at the boundary (3 independent comparisons). The
        // stub comment names only the SYMPTOM (a score of 90 grades B, not A) — never the operator or the fix, so the
        // model must read the comparisons + reason out the off-by-the-boundary bug. The check pins the exact boundary
        // scores (90/80/70) the buggy version gets wrong, so a partial fix still fails.
        "grade-boundaries" => (
            Solution: """
                #!/bin/sh
                # letter_grade SCORE: A>=90, B>=80, C>=70, D>=60, else F (0..100).
                # A score exactly ON a cutoff grades one letter too low (e.g. 90 returns B, but 90 should be A). Fix it.
                letter_grade() {
                  s=$1
                  if [ "$s" -gt 90 ]; then echo A
                  elif [ "$s" -gt 80 ]; then echo B
                  elif [ "$s" -gt 70 ]; then echo C
                  elif [ "$s" -ge 60 ]; then echo D
                  else echo F; fi
                }
                """,
            Check: """
                #!/bin/sh
                . ./solution.sh
                [ "$(letter_grade 90)" = "A" ] || exit 1
                [ "$(letter_grade 80)" = "B" ] || exit 1
                [ "$(letter_grade 70)" = "C" ] || exit 1
                [ "$(letter_grade 89)" = "B" ] || exit 1
                [ "$(letter_grade 60)" = "D" ] || exit 1
                [ "$(letter_grade 59)" = "F" ] || exit 1
                [ "$(letter_grade 100)" = "A" ] || exit 1
                """),

        // Algorithm: is_balanced needs a real depth-counter over the string (increment '(' / decrement ')' / fail on
        // negative or nonzero-at-end). The stub always says "yes"; the check includes unbalanced + wrong-order cases.
        "balanced-parens" => (
            Solution: """
                #!/bin/sh
                # is_balanced STR: echo "yes" if every '(' has a matching ')' IN ORDER, else "no". Round parens only.
                # The stub always echoes "yes". Implement the real check.
                is_balanced() { echo yes; }
                """,
            Check: """
                #!/bin/sh
                . ./solution.sh
                [ "$(is_balanced "()")" = "yes" ] || exit 1
                [ "$(is_balanced "(())")" = "yes" ] || exit 1
                [ "$(is_balanced "(()")" = "no" ] || exit 1
                [ "$(is_balanced ")(")" = "no" ] || exit 1
                [ "$(is_balanced "")" = "yes" ] || exit 1
                [ "$(is_balanced "(()())")" = "yes" ] || exit 1
                [ "$(is_balanced "())(")" = "no" ] || exit 1
                """),

        // Numeric algorithm: gcd needs Euclid's algorithm (the stub returns 1). The check includes coprime (17,5),
        // equal (7,7), and a ZERO operand (12,0 → 12): the zero case is the discriminator that rejects a
        // subtraction-based GCD (which is correct for two positives but never terminates on a zero operand — caught by
        // the grader's run timeout), so a half-robust version that skips the zero edge does not pass.
        "gcd-euclid" => (
            Solution: """
                #!/bin/sh
                # gcd A B: echo the greatest common divisor of two non-negative integers (Euclid's algorithm).
                # The stub returns a placeholder 1. Implement it.
                gcd() { echo 1; }
                """,
            Check: """
                #!/bin/sh
                . ./solution.sh
                [ "$(gcd 12 8)" = "4" ] || exit 1
                [ "$(gcd 17 5)" = "1" ] || exit 1
                [ "$(gcd 100 75)" = "25" ] || exit 1
                [ "$(gcd 48 36)" = "12" ] || exit 1
                [ "$(gcd 7 7)" = "7" ] || exit 1
                [ "$(gcd 12 0)" = "12" ] || exit 1
                """),

        // Edge-case bounding: clamp needs two comparisons AND must clamp correctly with a NEGATIVE range. The stub
        // returns X unbounded; the check pins below/above/inside the 0..10 range, plus a negative window that actually
        // clamps BOTH ways (-99 → -10 lower, 5 → -1 upper) — so a version that only clamps for non-negative bounds fails.
        "clamp-range" => (
            Solution: """
                #!/bin/sh
                # clamp X LO HI: echo X bounded to the inclusive range [LO, HI]. The stub returns X unbounded. Implement it.
                clamp() { echo "$1"; }
                """,
            Check: """
                #!/bin/sh
                . ./solution.sh
                [ "$(clamp 5 0 10)" = "5" ] || exit 1
                [ "$(clamp -3 0 10)" = "0" ] || exit 1
                [ "$(clamp 99 0 10)" = "10" ] || exit 1
                [ "$(clamp 0 0 10)" = "0" ] || exit 1
                [ "$(clamp 10 0 10)" = "10" ] || exit 1
                [ "$(clamp -5 -10 -1)" = "-5" ] || exit 1
                [ "$(clamp -99 -10 -1)" = "-10" ] || exit 1
                [ "$(clamp 5 -10 -1)" = "-1" ] || exit 1
                """),

        _ => throw new ArgumentException($"Unknown benchmark fixture '{fixtureRef}'. Known: failing-assertion, missing-function, off-by-one-loop, missing-guard, fizzbuzz, grade-boundaries, balanced-parens, gcd-euclid, clamp-range.", nameof(fixtureRef)),
    };

    /// <summary>Write a POSIX script 0755 (owner rwx, group/other r-x) so the runner can spawn / source it. A no-op on file mode where it doesn't apply.</summary>
    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
