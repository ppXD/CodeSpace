using CodeSpace.Core.Services.Supervisor;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit (pure function, no git): C3 — the derivation that gives the OPERATOR's acceptance floor a protected
/// oracle it never had to author. The rule is deliberately narrow: an acceptance command's PROGRAM file, and only
/// when the repository already ships that file at the graded base.
///
/// <para>The two boundaries this pins are the ones that decide whether the feature helps or harms. Protect too
/// little (miss the script behind <c>sh -c</c>) and a candidate rewrites its own judge unnoticed; protect too much
/// (a solution file passed as an ARGUMENT, or a script the candidate legitimately created) and honest work is
/// restored away and graded as a failure.</para>
/// </summary>
[Trait("Category", "Unit")]
public class AcceptanceOracleProtectionTests
{
    /// <summary>What the repository ships at the graded base — the existence oracle the derivation consults.</summary>
    private static readonly HashSet<string> AtBase = new(StringComparer.Ordinal)
    {
        "check.sh", "scripts/verify.sh", "tests/run.sh", "backend/App.sln", "Makefile", "tools/lint",
    };

    [Theory]
    // The judge itself, however it is spelled.
    [InlineData("./check.sh", "check.sh")]
    [InlineData("sh|check.sh", "check.sh")]
    [InlineData("bash|scripts/verify.sh", "scripts/verify.sh")]
    [InlineData("/usr/bin/bash|tests/run.sh", "tests/run.sh")]
    [InlineData("sh|-c|./check.sh --fast", "check.sh")]                 // the program hides inside the -c string
    [InlineData("sh|-c|npm ci && ./check.sh", "check.sh")]              // …behind a chained setup command
    [InlineData("sh|-c|./check.sh && ./check.sh", "check.sh")]          // …twice: deduped
    // …behind env assignments. Reading `CI=1` as the program would leave the real judge unprotected, and silently:
    // the assignment carries an `=`, so it is never a pathspec and nothing downstream would complain.
    [InlineData("sh|-c|CI=1 ./check.sh", "check.sh")]
    [InlineData("sh|-c|CI=1 TERM=dumb ./check.sh", "check.sh")]
    [InlineData("CI=1|sh|check.sh", "check.sh")]
    [InlineData("sh|-c|1BAD=x ./check.sh", "")]                         // not a valid assignment ⇒ it IS the program ⇒ not a pathspec
    // A binary on PATH is nobody's repo file.
    [InlineData("dotnet|test", "")]
    [InlineData("npm|run|test", "")]
    [InlineData("make|check", "")]
    // The SAFETY boundary: an argument is the code under test, never the judge. Restoring App.sln from base would
    // void a candidate that legitimately added a test project — a false FAILED on honest work.
    [InlineData("dotnet|test|backend/App.sln", "")]
    [InlineData("sh|check.sh|tests/run.sh", "check.sh")]
    // A program the repository does not ship at base is not the operator's judge (the candidate may have authored it).
    [InlineData("./missing.sh", "")]
    [InlineData("bash|scripts/absent.sh", "")]
    // Not a pathspec at all.
    [InlineData("/opt/ci/check.sh", "")]
    [InlineData("../outside/check.sh", "")]
    [InlineData("sh|-c|$JUDGE", "")]
    [InlineData("sh|-c|scripts/*.sh", "")]
    public void The_commands_own_program_is_the_oracle_and_nothing_else_is(string argv, string expected)
    {
        var derived = AcceptanceOracleProtection.DeriveProtectedPaths(Argv(argv), AtBase.Contains);

        derived.ShouldBe(Expected(expected));
    }

    [Fact]
    public void An_extensionless_program_is_still_derived_when_it_is_spelled_as_a_path()
    {
        // `tools/lint` has no extension, but the directory separator says it is a repo file, and the base confirms it.
        AcceptanceOracleProtection.DeriveProtectedPaths(Argv("tools/lint"), AtBase.Contains).ShouldBe(new[] { "tools/lint" });
    }

    [Fact]
    public void A_bare_word_program_is_never_probed_so_an_ordinary_floor_costs_nothing()
    {
        // Candidates are computed BEFORE the clone and decide whether the grade pays for full history + a base
        // probe. `dotnet test` must produce none of that — a bare word with no separator and no extension is a
        // binary on PATH.
        AcceptanceOracleProtection.ProgramCandidates(Argv("dotnet|test")).ShouldBeEmpty();
        AcceptanceOracleProtection.ProgramCandidates(Argv("sh|check.sh")).ShouldBe(new[] { "check.sh" }, "a script IS worth the probe");
    }

    [Fact]
    public void An_empty_or_absent_command_derives_nothing()
    {
        AcceptanceOracleProtection.DeriveProtectedPaths(null, _ => true).ShouldBeEmpty();
        AcceptanceOracleProtection.DeriveProtectedPaths(Array.Empty<string>(), _ => true).ShouldBeEmpty();
        AcceptanceOracleProtection.DeriveProtectedPaths(new[] { "  " }, _ => true).ShouldBeEmpty();
    }

    private static string[] Argv(string spec) => spec.Split('|', StringSplitOptions.RemoveEmptyEntries);

    private static string[] Expected(string spec) => spec.Length == 0 ? Array.Empty<string>() : spec.Split(',');
}
