using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit (pure function, no git): C3 — the derivation that gives the OPERATOR's acceptance floor a protected
/// oracle it never had to author. The rule is deliberately narrow: an acceptance command's PROGRAM file, only when
/// the repository already ships that file at the graded base, and only when the run's own floor runs it too.
///
/// <para>The boundaries this pins are the ones that decide whether the feature helps or harms. Protect too little
/// (miss the script behind <c>sh -c</c>) and a candidate rewrites its own judge unnoticed; protect too much (a
/// solution file passed as an ARGUMENT, a script the candidate legitimately created, or a deliverable the check
/// happens to EXECUTE) and honest work is restored away and graded as a failure — which a live run proved is not
/// hypothetical. Hence the third boundary: a derived program file is the run's judge only when the OPERATOR FLOOR
/// runs that same file; anything else the command executes is the subject under test.</para>
/// </summary>
[Trait("Category", "Unit")]
public class AcceptanceOracleProtectionTests
{
    /// <summary>What the repository ships at the graded base — the existence oracle the derivation consults.</summary>
    private static readonly HashSet<string> AtBase = new(StringComparer.Ordinal)
    {
        "check.sh", "scripts/verify.sh", "tests/run.sh", "backend/App.sln", "Makefile", "tools/lint",
    };

    /// <summary>The run's own ORACLE INVENTORY for the extraction cases below: every file the base ships is stipulated to be a floor program, so these cases isolate the EXTRACTION rule. Which files the run actually owns is the separate boundary the narrowing Theory at the bottom pins.</summary>
    private static readonly string[] OwnsEverythingAtBase = AtBase.ToArray();

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
        var derived = AcceptanceOracleProtection.DeriveProtectedPaths(Argv(argv), OwnsEverythingAtBase, AtBase.Contains);

        derived.ShouldBe(Expected(expected));
    }

    [Fact]
    public void An_extensionless_program_is_still_derived_when_it_is_spelled_as_a_path()
    {
        // `tools/lint` has no extension, but the directory separator says it is a repo file, and the base confirms it.
        AcceptanceOracleProtection.DeriveProtectedPaths(Argv("tools/lint"), OwnsEverythingAtBase, AtBase.Contains).ShouldBe(new[] { "tools/lint" });
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
        AcceptanceOracleProtection.DeriveProtectedPaths(null, OwnsEverythingAtBase, _ => true).ShouldBeEmpty();
        AcceptanceOracleProtection.DeriveProtectedPaths(Array.Empty<string>(), OwnsEverythingAtBase, _ => true).ShouldBeEmpty();
        AcceptanceOracleProtection.DeriveProtectedPaths(new[] { "  " }, OwnsEverythingAtBase, _ => true).ShouldBeEmpty();
    }

    private static string[] Argv(string spec) => spec.Split('|', StringSplitOptions.RemoveEmptyEntries);

    private static string[] Expected(string spec) => spec.Length == 0 ? Array.Empty<string>() : spec.Split(',');

    // ── MayProtect: the RESTORE-BASE decision — the one guard shared by the grader (widen the clone before the
    // restore) and SupervisorTurnService.Rehydrate.cs's per-unit OracleBaseShaAsync (resolve a base sha worth
    // restoring from at all). An authored-only guard there meant a per-unit oracle whose only protection was
    // DERIVED — the shape every real operator floor has, since nothing in Core or the UI ever authors
    // ProtectedPaths — never got a base sha to restore from in the first place.
    //
    // The second boundary is what a live run cost us: the derived half must ALSO be narrowed to oracle files the
    // run OWNS. The brain authored a per-unit check whose argv named the deliverable itself (`sh solution.sh 7 5`
    // against a goal that said "edit solution.sh"), so the derived protection restored the stub over a correct
    // agent's work and voided it — "the check itself protects the very file the goal requires editing, so no
    // retry can pass it". A program file the run's own floor never runs is the SUBJECT under test, not a judge. ──

    /// <summary>The run's oracle inventory in these cases: the operator floor is `sh check.sh`, so the run owns exactly check.sh.</summary>
    private static readonly string[] FloorOwnsCheckScript = { "check.sh" };

    [Theory]
    // authored ProtectedPaths outrank the command outright — protectable even though `dotnet test` alone derives nothing, and even with no floor
    [InlineData(true, "dotnet|test", true, true)]
    [InlineData(true, "dotnet|test", false, true)]
    // the FLOOR's own file, named by a per-unit command: the realistic operator-floor shape, restorable
    [InlineData(false, "sh|check.sh", true, true)]
    // the SAME command with no floor to own it — a run whose operator configured no executable floor owns no judge
    [InlineData(false, "sh|check.sh", false, false)]
    // the regression: a per-unit command executing the DELIVERABLE. Not floor-owned ⇒ never restored (the grader notes it instead)
    [InlineData(false, "sh|solution.sh|7|5", true, false)]
    // a program the run does not own however it is spelled, including behind `-c`
    [InlineData(false, "sh|-c|python main.py", true, false)]
    // nothing derivable at all — no program file in the argv, so nothing to own
    [InlineData(false, "dotnet|test", true, false)]
    public void A_restore_base_is_worth_resolving_only_for_an_oracle_the_run_owns(bool authored, string argv, bool hasFloor, bool expected)
    {
        var spec = new SupervisorAcceptanceSpec { Command = Argv(argv), ProtectedPaths = authored ? new[] { "tests/" } : null };

        AcceptanceOracleProtection.MayProtect(spec, hasFloor ? FloorOwnsCheckScript : null).ShouldBe(expected);
    }

    [Theory]
    [InlineData("sh|check.sh", "check.sh", "")]                       // the floor's judge: protected, and NOT the subject
    [InlineData("sh|solution.sh|7|5", "", "solution.sh")]             // the deliverable under test: never protected, reported instead
    [InlineData("sh|-c|./check.sh && python main.py", "check.sh", "main.py")]   // both at once — a judge AND a subject, each named for what it is
    [InlineData("dotnet|test", "", "")]                               // no repo file either way: the quiet, dominant case
    public void A_commands_program_files_split_into_the_runs_judge_and_the_subject_under_test(string argv, string expectedOwned, string expectedSubject)
    {
        var spec = new SupervisorAcceptanceSpec { Command = Argv(argv) };

        var owned = AcceptanceOracleProtection.CommandOracleCandidates(spec, FloorOwnsCheckScript);

        owned.ShouldBe(Expected(expectedOwned));
        AcceptanceOracleProtection.CommandProgramCandidates(spec).Where(p => !owned.Contains(p)).ShouldBe(Expected(expectedSubject),
            "what the grader reports as the SUBJECT is exactly the program files it did not protect");
    }

    [Fact]
    public void A_program_the_candidate_created_is_not_protected_even_when_the_floor_names_it()
    {
        // The scope fence that predates the narrowing and must survive it: the base does not ship it, so the
        // candidate authored it ("add a check" work) and `git checkout <base> -- <path>` would fail outright.
        AcceptanceOracleProtection.DeriveProtectedPaths(Argv("sh|missing.sh"), new[] { "missing.sh" }, AtBase.Contains).ShouldBeEmpty();
        AcceptanceOracleProtection.DeriveProtectedPaths(Argv("sh|check.sh"), FloorOwnsCheckScript, AtBase.Contains).ShouldBe(new[] { "check.sh" }, "the same floor file the base DOES ship is protected");
    }

    [Fact]
    public void A_non_argv_oracle_owns_nothing_however_the_floor_is_configured()
    {
        // ArtifactPresent's Command is the list of deliverables the candidate must PRODUCE. Restoring one of them
        // would void the very work being verified — so the kind gate holds even when the floor names the same path.
        var spec = new SupervisorAcceptanceSpec { Command = new[] { "check.sh" }, Kind = BenchmarkGradingKind.ArtifactPresent };

        AcceptanceOracleProtection.CommandOracleCandidates(spec, FloorOwnsCheckScript).ShouldBeEmpty();
        AcceptanceOracleProtection.CommandProgramCandidates(spec).ShouldBeEmpty("not even as a subject — a deliverable is not a program the check runs");
        AcceptanceOracleProtection.MayProtect(spec, FloorOwnsCheckScript).ShouldBeFalse();
    }

    [Fact]
    public void The_floor_inventory_comes_from_the_same_extraction_the_narrowing_applies()
    {
        // One derivation, not two: a caller turns the operator's argv into the inventory with ProgramCandidates,
        // which is the same function whose output the narrowing filters — so `["sh","check.sh"]` and `./check.sh`
        // cannot disagree about what the run owns.
        var floor = AcceptanceOracleProtection.ProgramCandidates(Argv("sh|check.sh"));

        floor.ShouldBe(new[] { "check.sh" });
        AcceptanceOracleProtection.CommandOracleCandidates(new SupervisorAcceptanceSpec { Command = Argv("./check.sh|--ci") }, floor).ShouldBe(new[] { "check.sh" });
    }
}
