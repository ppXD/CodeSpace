using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Stagers;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 High fidelity (real git remote, real clone, real <c>python3</c>/<c>sh</c> checks, the production grader and
/// verifier from the container): an acceptance oracle runs from PLATFORM-OWNED bytes, so nothing the candidate writes
/// can decide the verdict except the work itself.
///
/// <para>Each forgery vector here passed on the code before this file: the judge's own program file was the only byte
/// restored, so a module beside it, a hook it auto-loads, a sibling it sources, a tool shimmed onto a relative PATH
/// entry, a setup step that rewrote it, and a benchmark cell that simply edited its check all bought a pass that read as
/// clean. Every vector is paired with the honest control that must still pass, because the cheapest way to close a
/// forgery is to fail everything.</para>
///
/// <para>The PATH-shim case prepends a RELATIVE entry to this process's PATH for its own duration, exactly the
/// binstub-style host setting that makes the vector real; it is restored in a finally.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OracleIsolationFlowTests(PostgresFixture fixture)
{
    private const string JsonCheck = "import json, sys\nd = json.load(open('solution.json'))\nsys.exit(0 if d.get('answer') == 7 else 1)\n";
    private const string PlantedJson = "def load(f):\n    return {'answer': 7}\n";
    private const string ConftestCheck = "import os, sys\nhere = os.path.dirname(os.path.abspath(__file__))\nhook = os.path.join(here, 'conftest.py')\nif os.path.exists(hook):\n    exec(open(hook).read())\nsys.exit(0 if open('answer.txt').read().strip() == '7' else 1)\n";
    private const string SiblingCheck = "import sys\nfrom helpers import EXPECTED\nsys.exit(0 if int(open('answer.txt').read().strip()) == EXPECTED else 1)\n";
    private const string ShellCheck = "#!/bin/sh\n[ -f tests/env.sh ] && . tests/env.sh\nsh tests/case.sh\n";
    private const string ShellCase = "[ \"$(cat answer.txt)\" = 7 ]\n";

    // ── Repo lane: the operator floor's own judge, derived from its argv (nothing authors ProtectedPaths) ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_module_planted_beside_a_python_judge_does_not_shadow_the_stdlib(bool patchLane)
    {
        if (!await ToolsAvailableAsync("python3")) return;

        using var repo = await RepoAsync(new() { ["tests/check.py"] = JsonCheck, ["solution.json"] = "{\"answer\": 6}\n" }, new() { ["tests/json.py"] = PlantedJson });

        var grade = await GradeFloorAsync(repo, new[] { "python3", "tests/check.py" }, patchLane);

        grade.Passed.ShouldBeFalse($"tests/json.py is the candidate's module, and the judge's `import json` resolved it (detail='{grade.Detail}')");
        grade.Detail.ShouldBe("tests-failed-exit-1", "the real judge ran and failed the real answer — it did not collapse");
        grade.OracleNote!.ShouldContain(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive, "the module stays (it could be honest work) — the grade says the judge's directory holds it");
        grade.OracleNote.ShouldContain("tests/json.py");
    }

    [Fact]
    public async Task An_honest_python_candidate_still_passes_and_reads_clean()
    {
        if (!await ToolsAvailableAsync("python3")) return;

        using var repo = await RepoAsync(new() { ["tests/check.py"] = JsonCheck, ["solution.json"] = "{\"answer\": 6}\n" }, new() { ["solution.json"] = "{\"answer\": 7}\n" });

        var grade = await GradeFloorAsync(repo, new[] { "python3", "tests/check.py" });

        grade.Passed.ShouldBeTrue($"the candidate fixed the answer the judge reads (detail='{grade.Detail}', note='{grade.OracleNote}')");
        grade.Detail.ShouldBe("tests-passed");
        grade.OracleNote.ShouldBeNull("the judge's directory is the base's, byte for byte, and its runtime ran isolated — nothing to report");
    }

    [Fact]
    public async Task A_conftest_the_judge_auto_loads_from_its_directory_cannot_be_planted()
    {
        if (!await ToolsAvailableAsync("python3")) return;

        using var repo = await RepoAsync(new() { ["tests/check.py"] = ConftestCheck, ["answer.txt"] = "6\n" }, new() { ["tests/conftest.py"] = "import sys\nsys.exit(0)\n" });

        var grade = await GradeFloorAsync(repo, new[] { "python3", "tests/check.py" });

        grade.Passed.ShouldBeFalse("a hook a runtime loads purely by its presence is never left beside the judge");
        grade.Class.ShouldBe(GradeFailureClass.Genuine);
        grade.OracleNote!.ShouldContain("discarded: tests/conftest.py", Case.Sensitive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sibling_module_the_candidate_edits_beside_a_judge_is_kept_and_never_reads_clean(bool honest)
    {
        if (!await ToolsAvailableAsync("python3")) return;

        var candidate = honest ? new Dictionary<string, string> { ["answer.txt"] = "7\n" } : new Dictionary<string, string> { ["tests/helpers.py"] = "EXPECTED = 6\n" };
        using var repo = await RepoAsync(new() { ["tests/check.py"] = SiblingCheck, ["tests/helpers.py"] = "EXPECTED = 7\n", ["answer.txt"] = "6\n" }, candidate);

        var grade = await GradeFloorAsync(repo, new[] { "python3", "tests/check.py" });

        grade.Passed.ShouldBeTrue($"detail='{grade.Detail}' note='{grade.OracleNote}'");

        if (honest)
        {
            grade.OracleNote.ShouldBeNull();
            return;
        }

        // Restoring the sibling would void honest edits beside a judge (an expected output the goal asked to update);
        // the edit stays, and the pass must say its judge read bytes the platform does not own.
        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive);
        grade.OracleNote.ShouldContain("tests/helpers.py");
    }

    [Theory]
    [InlineData("add-sourced-hook", "tests/env.sh")]
    [InlineData("edit-executed-case", "tests/case.sh")]
    [InlineData("honest", null)]
    public async Task A_shell_judge_never_reads_clean_and_names_what_the_candidate_left_beside_it(string scenario, string? kept)
    {
        if (!await ToolsAvailableAsync("sh")) return;

        var candidate = scenario switch
        {
            "add-sourced-hook" => new Dictionary<string, string> { ["tests/env.sh"] = "exit 0\n" },
            "edit-executed-case" => new Dictionary<string, string> { ["tests/case.sh"] = "exit 0\n" },
            _ => new Dictionary<string, string> { ["answer.txt"] = "7\n" },
        };
        using var repo = await RepoAsync(new() { ["tests/check.sh"] = ShellCheck, ["tests/case.sh"] = ShellCase, ["answer.txt"] = "6\n" }, candidate);

        var grade = await GradeFloorAsync(repo, new[] { "sh", "tests/check.sh" });

        grade.Passed.ShouldBeTrue($"detail='{grade.Detail}' note='{grade.OracleNote}'");
        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive, "a shell judge starts its commands in the graded tree — no shell-judged pass reads clean");
        grade.OracleNote.ShouldNotContain("TAMPER", Case.Sensitive);

        if (kept is not null) grade.OracleNote.ShouldContain(kept);
    }

    [Fact]
    public async Task A_pinned_shell_judge_whose_inline_python_a_root_module_shadows_is_labelled()
    {
        // The audit vector one process down: the judge's own bytes are pinned, but the `python3 -c` it starts runs with
        // the candidate's root as its working directory, so the candidate's json.py answers `import json`.
        if (!await ToolsAvailableAsync("sh", "python3")) return;

        var check = "#!/bin/sh\npython3 -c \"import json, sys; sys.exit(0 if json.load(open('solution.json'))['answer'] == 7 else 1)\"\n";
        using var repo = await RepoAsync(new() { ["tests/check.sh"] = check, ["solution.json"] = "{\"answer\": 6}\n" }, new() { ["json.py"] = PlantedJson });

        var grade = await GradeFloorAsync(repo, new[] { "sh", "tests/check.sh" });

        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive, $"whatever the verdict (passed={grade.Passed}), a shell judge's children are not isolated and the grade says so");
        grade.OracleNote.ShouldContain("working directory");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_tool_shimmed_onto_a_relative_PATH_entry_cannot_answer_for_the_judge(bool honest)
    {
        if (!await ToolsAvailableAsync("sh", "cmp")) return;

        var candidate = new Dictionary<string, string> { ["out.txt"] = honest ? "7\n" : "6\n", ["tools/cmp"] = "#!/bin/sh\nexit 0\n" };
        using var repo = await RepoAsync(new() { ["tests/check.sh"] = "#!/bin/sh\ncmp -s tests/expected.txt out.txt\n", ["tests/expected.txt"] = "7\n" }, candidate, executables: new[] { "tools/cmp" });

        var original = Environment.GetEnvironmentVariable("PATH");
        BenchmarkGrade grade;

        try
        {
            // A binstub-style host: a RELATIVE PATH entry, resolved against whatever directory the check runs in.
            Environment.SetEnvironmentVariable("PATH", $"tools{Path.PathSeparator}{original}");

            grade = await GradeFloorAsync(repo, new[] { "sh", "tests/check.sh" });
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }

        grade.Passed.ShouldBe(honest, $"`cmp` must be the host's, never the candidate's tools/cmp (detail='{grade.Detail}')");
    }

    [Fact]
    public async Task A_setup_step_cannot_rewrite_the_judge_after_it_was_restored()
    {
        if (!await ToolsAvailableAsync("sh")) return;

        // The setup step runs the candidate's own code (an `npm ci` lifecycle script, a `pip install -e .`); here it
        // is a script the candidate committed, which rewrites the very case the judge runs.
        using var repo = await RepoAsync(new() { ["tests/check.sh"] = ShellCheck, ["tests/case.sh"] = ShellCase, ["answer.txt"] = "6\n" }, new() { ["setup.sh"] = "printf 'exit 0\\n' > tests/case.sh\n" });

        var spec = new SupervisorAcceptanceSpec { Command = new[] { "sh", "tests/check.sh" }, SetupCommand = new[] { "sh", "setup.sh" } };
        var grade = await GradeSpecAsync(repo, spec, patchLane: false);

        grade.Passed.ShouldBeFalse($"the setup rewrote tests/case.sh after the restore — the judge must still run the base's bytes (detail='{grade.Detail}')");
        grade.OracleNote.ShouldNotBeNull();
        grade.OracleNote!.ShouldContain("setup step changed platform-owned judge bytes", Case.Sensitive, "the operator must learn the setup step touched the judge");
        grade.OracleNote.ShouldContain("tests/case.sh");
    }

    [Theory]
    [InlineData("json.py", false)]       // an ordinary module the candidate's .gitignore hides — kept, so it can never lead the judge's path
    [InlineData("conftest.py", true)]    // a hook — put back to base whatever the .gitignore says
    public async Task A_setup_step_cannot_hide_a_plant_beside_the_judge_behind_the_candidates_gitignore(string planted, bool hook)
    {
        // `git clean` without -x keeps ignored files, and the candidate writes the .gitignore: a setup that writes an
        // ignored module beside a python judge used to survive the post-setup reassert and grade Clean.
        if (!await ToolsAvailableAsync("sh", "python3")) return;

        var judge = hook ? ConftestCheck : JsonCheck;
        var payload = hook ? "import sys\\nsys.exit(0)\\n" : "def load(f):\\n    return {\"answer\": 7}\\n";
        using var repo = await RepoAsync(
            new() { ["tests/check.py"] = judge, ["solution.json"] = "{\"answer\": 6}\n", ["answer.txt"] = "6\n" },
            new() { [".gitignore"] = $"tests/{planted}\n", ["setup.sh"] = $"printf '{payload}' > tests/{planted}\n" });

        var spec = new SupervisorAcceptanceSpec { Command = new[] { "python3", "tests/check.py" }, SetupCommand = new[] { "sh", "setup.sh" } };
        var grade = await GradeSpecAsync(repo, spec, patchLane: false);

        grade.Passed.ShouldBeFalse($"the plant must not decide the verdict (detail='{grade.Detail}', note='{grade.OracleNote}')");
        grade.OracleNote!.ShouldContain($"tests/{planted}", Case.Sensitive, "and the grade names what the setup left beside the judge");
    }

    [Fact]
    public async Task A_setup_step_that_builds_beside_the_judge_keeps_its_output_and_says_so()
    {
        // An honest setup that generates the judge's case into its own directory used to have its output deleted by the
        // post-setup clean, failing a correct candidate with nothing to say why.
        if (!await ToolsAvailableAsync("sh")) return;

        using var repo = await RepoAsync(
            new() { ["tests/run.sh"] = "#!/bin/sh\nsh tests/generated_case.sh\n", ["build.sh"] = "printf '[ \"$(cat answer.txt)\" = 7 ]\\n' > tests/generated_case.sh\n", ["answer.txt"] = "6\n" },
            new() { ["answer.txt"] = "7\n" });

        var spec = new SupervisorAcceptanceSpec { Command = new[] { "sh", "tests/run.sh" }, SetupCommand = new[] { "sh", "build.sh" } };
        var grade = await GradeSpecAsync(repo, spec, patchLane: false);

        grade.Passed.ShouldBeTrue($"the setup's output is what the judge runs (detail='{grade.Detail}', note='{grade.OracleNote}')");
        grade.OracleNote!.ShouldContain("tests/generated_case.sh", Case.Sensitive);
    }

    [Theory]
    [InlineData("chmod")]
    [InlineData("unlink")]
    public async Task A_subject_that_rewrites_its_sealed_judge_mid_grade_loses_the_pass(string how)
    {
        // The seal is only a speed bump — the check runs as the files' owner — so the scope is compared after the check.
        if (!await ToolsAvailableAsync("sh", "cmp")) return;

        var forge = how == "chmod" ? "chmod u+w tests/expected.txt; echo 6 > tests/expected.txt\necho 6\n" : "rm -f tests/expected.txt; echo 6 > tests/expected.txt\necho 6\n";
        using var repo = await RepoAsync(new() { ["tests/check.sh"] = "#!/bin/sh\nsh solution.sh > out.txt\ncmp -s out.txt tests/expected.txt\n", ["tests/expected.txt"] = "7\n", ["solution.sh"] = "echo 6\n" }, new() { ["solution.sh"] = forge });

        var grade = await GradeFloorAsync(repo, new[] { "sh", "tests/check.sh" });

        grade.Passed.ShouldBeFalse($"the verdict was decided against a judge the subject rewrote (detail='{grade.Detail}', note='{grade.OracleNote}')");
        grade.Detail.ShouldBe(OracleGuard.ChangedDuringCheckDetail);
        grade.Class.ShouldBe(GradeFailureClass.Genuine);
        grade.OracleNote!.ShouldContain(OracleGuard.TamperNoteMarker, Case.Sensitive);
        grade.OracleNote.ShouldContain("tests/expected.txt");
    }

    // ── Repo lane, honest controls: work that lives beside the judge is never reverted ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_co_located_test_still_grades_the_candidates_fix_beside_it(bool patchLane)
    {
        if (!await ToolsAvailableAsync("python3")) return;

        using var repo = await RepoAsync(
            new() { ["src/test_calc.py"] = "import sys\nfrom calc import add\nsys.exit(0 if add(2, 3) == 5 else 1)\n", ["src/calc.py"] = "def add(a, b):\n    return a - b\n" },
            new() { ["src/calc.py"] = "def add(a, b):\n    return a + b\n" });

        var grade = await GradeFloorAsync(repo, new[] { "python3", "src/test_calc.py" }, patchLane);

        grade.Passed.ShouldBeTrue($"src/calc.py is the work, not the judge (detail='{grade.Detail}', note='{grade.OracleNote}')");
        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive, "the judge imports bytes the platform does not own, and says so");
        grade.OracleNote.ShouldContain("src/calc.py");
        grade.OracleNote.ShouldNotContain("TAMPER", Case.Sensitive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_expected_output_the_goal_asked_to_update_is_kept(bool patchLane)
    {
        if (!await ToolsAvailableAsync("sh")) return;

        using var repo = await RepoAsync(
            new() { ["tests/check.sh"] = "#!/bin/sh\n[ \"$(sh greet.sh)\" = \"$(cat tests/expected.txt)\" ]\n", ["tests/expected.txt"] = "Hello\n", ["greet.sh"] = "echo Hello\n" },
            new() { ["greet.sh"] = "echo Hi\n", ["tests/expected.txt"] = "Hi\n" });

        var grade = await GradeFloorAsync(repo, new[] { "sh", "tests/check.sh" }, patchLane);

        grade.Passed.ShouldBeTrue($"detail='{grade.Detail}' note='{grade.OracleNote}'");
        grade.OracleNote!.ShouldContain("tests/expected.txt");
        grade.OracleNote.ShouldNotContain("TAMPER", Case.Sensitive);
    }

    [Fact]
    public async Task A_subject_the_judge_runs_from_its_own_directory_is_graded_as_the_candidate_wrote_it()
    {
        if (!await ToolsAvailableAsync("sh")) return;

        using var repo = await RepoAsync(new() { ["scripts/check.sh"] = "#!/bin/sh\n[ \"$(sh scripts/tool.sh)\" = ok ]\n", ["scripts/tool.sh"] = "echo broken\n" }, new() { ["scripts/tool.sh"] = "echo ok\n" });

        var grade = await GradeFloorAsync(repo, new[] { "sh", "scripts/check.sh" });

        grade.Passed.ShouldBeTrue($"detail='{grade.Detail}' note='{grade.OracleNote}'");
        grade.OracleNote!.ShouldContain("scripts/tool.sh");
        grade.OracleNote.ShouldNotContain("TAMPER", Case.Sensitive);
    }

    [Theory]
    [InlineData("-2", true)]     // the regression test the goal asked for, correct
    [InlineData("-3", false)]    // the same test asserting a wrong sum — it fails, which proves it RAN rather than being deleted
    public async Task A_regression_test_the_goal_asked_for_runs_beside_the_judge(string expectedSum, bool expectedPass)
    {
        if (!await ToolsAvailableAsync("sh")) return;

        using var repo = await RepoAsync(
            new() { ["tests/run.sh"] = "#!/bin/sh\nfor t in tests/test_*.sh; do sh \"$t\" || exit 1; done\n", ["tests/test_basic.sh"] = "[ \"$(sh add.sh 1 1)\" = 2 ]\n", ["add.sh"] = "echo $(( $1 + $2 ))\n" },
            new() { ["tests/test_negative.sh"] = $"[ \"$(sh add.sh -1 -1)\" = {expectedSum} ]\n" });

        var grade = await GradeFloorAsync(repo, new[] { "sh", "tests/run.sh" });

        grade.Passed.ShouldBe(expectedPass, $"detail='{grade.Detail}' note='{grade.OracleNote}'");
        grade.OracleNote!.ShouldContain("tests/test_negative.sh");
        grade.OracleNote.ShouldNotContain("TAMPER", Case.Sensitive);
    }

    [Fact]
    public async Task A_judge_at_the_repository_root_is_labelled_unverified_never_clean()
    {
        if (!await ToolsAvailableAsync("sh")) return;

        // The judge's directory IS the repository, so only its own file can be pinned: the files it sources from the
        // tree are indistinguishable from the work. That pass may stand, but it must not read like a protected one.
        using var repo = await RepoAsync(new() { ["check.sh"] = ShellCheck, ["tests/case.sh"] = "exit 1\n" }, new() { ["tests/env.sh"] = "exit 0\n" });

        var grade = await GradeFloorAsync(repo, new[] { "sh", "check.sh" });

        grade.OracleNote.ShouldNotBeNull("a forgeable verdict must say so on the grade itself");
        grade.OracleNote!.ShouldContain(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive);
        grade.OracleNote.ShouldContain("repository root");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_runner_that_reads_the_candidates_config_is_labelled_unverified_however_it_is_spelled(bool absolute)
    {
        if (!await ToolsAvailableAsync("make", "sh")) return;

        var make = absolute ? (await CommandPathAsync("make")) : "make";
        using var repo = await RepoAsync(new() { ["Makefile"] = "check:\n\tsh tests/case.sh\n", ["tests/case.sh"] = "exit 1\n" }, new() { ["Makefile"] = "check:\n\ttrue\n" });

        var grade = await GradeFloorAsync(repo, new[] { make, "check" });

        grade.OracleNote.ShouldNotBeNull($"`{make}` reads the candidate's Makefile — the grade cannot claim an isolated judge");
        grade.OracleNote!.ShouldContain(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive);
        grade.OracleNote.ShouldContain("`make`");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_pinned_node_judge_that_resolves_the_candidates_node_modules_is_labelled(bool forge)
    {
        if (!await ToolsAvailableAsync("node")) return;

        var candidate = forge
            ? new Dictionary<string, string> { ["node_modules/chai/index.js"] = "module.exports = { assert: { equal() {} } };\n", ["node_modules/chai/package.json"] = "{\"name\":\"chai\",\"main\":\"index.js\"}\n" }
            : new Dictionary<string, string> { ["answer.txt"] = "7\n" };
        using var repo = await RepoAsync(new() { ["tests/check.js"] = "const { assert } = require('chai');\nassert.equal(require('fs').readFileSync('answer.txt', 'utf8').trim(), '7');\n", ["answer.txt"] = "6\n" }, candidate);

        var grade = await GradeFloorAsync(repo, new[] { "node", "tests/check.js" });

        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive, $"node resolves `chai` up into the candidate's tree, so no node-judged verdict reads clean (passed={grade.Passed})");
        grade.OracleNote.ShouldContain("node_modules");
    }

    [Theory]
    [InlineData("calendar.py", "def is_leap(y):\n    return y % 4 == 0 and (y % 100 != 0 or y % 400 == 0)\n", "import sys\nfrom calendar import is_leap\nsys.exit(0 if is_leap(2000) and not is_leap(1900) else 1)\n", "calendar")]
    [InlineData("statistics.py", "def median(xs):\n    return xs[0]\n", "import sys\nfrom statistics import median\nsys.exit(0 if median([3, 1, 2]) == 2 else 1)\n", "statistics")]
    public async Task An_inline_python_floor_whose_subject_shares_a_stdlib_name_is_labelled(string module, string source, string check, string named)
    {
        if (!await ToolsAvailableAsync("python3")) return;

        using var repo = await RepoAsync(new() { ["README.md"] = "base\n" }, new() { [module] = source });

        var grade = await GradeFloorAsync(repo, new[] { "python3", "-c", check });

        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive, $"the verdict (passed={grade.Passed}) is of the interpreter's {named}, not the candidate's — it must say so");
        grade.OracleNote.ShouldContain(named);
    }

    // ── Scratch lane: a repo-less run graded in its own live workspace ──

    [Theory]
    [InlineData("planted-json", false)]
    [InlineData("honest", true)]
    [InlineData("imports-its-own-module", true)]
    public async Task An_inline_python_check_ignores_a_module_planted_in_the_scratch(string scenario, bool expected)
    {
        if (!await ToolsAvailableAsync("python3")) return;

        var argv = scenario == "imports-its-own-module"
            ? new[] { "python3", "-c", "import sys\nfrom answer import VALUE\nsys.exit(0 if VALUE == 7 else 1)\n" }
            : new[] { "python3", "-c", "import json, sys\nsys.exit(0 if json.load(open('out.json'))['answer'] == 7 else 1)\n" };
        using var seed = await SeedScratchAsync(argv);
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);

        // "The agent runs": it writes its deliverable and, in the forgery, a module beside it.
        await File.WriteAllTextAsync(Path.Combine(seed.Directory, "out.json"), scenario == "honest" ? "{\"answer\": 7}\n" : "{\"answer\": 6}\n");
        await File.WriteAllTextAsync(Path.Combine(seed.Directory, "answer.py"), "VALUE = 7\n");
        if (scenario == "planted-json") await File.WriteAllTextAsync(Path.Combine(seed.Directory, "json.py"), PlantedJson);

        var grade = await verifier.GradeAsync(seed.Request(context), CancellationToken.None);

        grade.Passed.ShouldBe(expected, $"detail='{grade.Detail}' note='{grade.OracleNote}'");
    }

    [Fact]
    public async Task An_inline_python_check_of_a_stdlib_named_scratch_subject_says_which_module_it_graded()
    {
        if (!await ToolsAvailableAsync("python3")) return;

        using var seed = await SeedScratchAsync(new[] { "python3", "-c", "import sys\nfrom calendar import is_leap\nsys.exit(0 if is_leap(2000) and not is_leap(1900) else 1)\n" });
        using var scope = fixture.BeginScope();
        var verifier = scope.Resolve<LocalAcceptanceVerifier>();
        using var context = await verifier.PrepareAsync(seed.Preparation, CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(seed.Directory, "calendar.py"), "def is_leap(y):\n    return y % 4 == 0 and (y % 100 != 0 or y % 400 == 0)\n");

        var grade = await verifier.GradeAsync(seed.Request(context), CancellationToken.None);

        grade.OracleNote!.ShouldContain(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive, $"the stdlib's calendar answered the import (passed={grade.Passed}), and the grade must say so");
        grade.OracleNote.ShouldContain("calendar");
    }

    // ── Benchmark lane: a cell graded against its frozen fixture ──

    [Theory]
    [InlineData("edit-judge", false)]
    [InlineData("honest", true)]
    [InlineData("honest-and-edit-judge", true)]
    public async Task An_edited_judge_cannot_grade_a_seed_cell_solved(string scenario, bool expected)
    {
        if (!await ToolsAvailableAsync("sh")) return;

        var task = SeedBenchmarkCorpus.Tasks.First(t => t.FixtureRef == "failing-assertion");
        using var workspace = new TempDirectory("cs-oracle-bench-");
        SeedBenchmarkFixtures.Stage(task.FixtureRef, workspace.Path);

        if (scenario.Contains("edit-judge")) await File.WriteAllTextAsync(Path.Combine(workspace.Path, "check.sh"), "#!/bin/sh\nexit 0\n");
        if (scenario.StartsWith("honest")) await File.WriteAllTextAsync(Path.Combine(workspace.Path, "solution.sh"), "#!/bin/sh\nREPORTED_SUM=5\n");

        var grade = await GradeBenchmarkAsync(task, workspace.Path, new SeedFixtureStager());

        grade.Passed.ShouldBe(expected, $"detail='{grade.Detail}' note='{grade.OracleNote}'");

        var tampered = scenario.Contains("edit-judge");
        (grade.OracleNote ?? "").Contains("TAMPER VOIDED", StringComparison.Ordinal).ShouldBe(tampered, $"a cell that touched its judge is flagged, and only such a cell is (note='{grade.OracleNote}')");

        if (tampered) grade.OracleNote!.ShouldContain("check.sh");
    }

    [Theory]
    [InlineData("edit-judge", false)]
    [InlineData("honest", true)]
    public async Task A_hidden_suite_judge_runs_from_the_frozen_fixture(string scenario, bool expected)
    {
        if (!await ToolsAvailableAsync("sh")) return;

        using var suiteDirectory = new TempDirectory("cs-oracle-suite-");
        var reference = "development/" + Guid.NewGuid().ToString("N");
        var task = new BenchmarkTask
        {
            Id = "sum", Description = "a hidden-suite-shaped fixture whose judge runs the subject in a child process", FixtureRef = reference, Goal = "make sum.sh add its operands",
            Grading = BenchmarkGradingKind.TestsPass, Harness = "codex-cli", Modes = new[] { BenchmarkMode.HarnessCli }, TestCommand = new[] { "sh", "tests/run_tests.sh" }, TimeoutSeconds = 30,
        };
        WriteFile(suiteDirectory.Path, $"fixtures/{reference}/tests/run_tests.sh", "#!/bin/sh\n[ \"$(sh src/sum.sh 2 2)\" = 4 ]\n");
        WriteFile(suiteDirectory.Path, $"fixtures/{reference}/src/sum.sh", "#!/bin/sh\necho 0\n");
        WriteFile(suiteDirectory.Path, "tasks.json", JsonSerializer.Serialize(new[] { task }, AgentJson.Options));
        var suite = HiddenSuiteLoader.Load(suiteDirectory.Path);

        using var workspace = new TempDirectory("cs-oracle-hidden-");
        suite.FixtureStager.Stage(reference, workspace.Path);

        if (scenario == "edit-judge") await File.WriteAllTextAsync(Path.Combine(workspace.Path, "tests", "run_tests.sh"), "#!/bin/sh\nexit 0\n");
        else await File.WriteAllTextAsync(Path.Combine(workspace.Path, "src", "sum.sh"), "#!/bin/sh\necho $(($1 + $2))\n");

        var grade = await GradeBenchmarkAsync(suite.Tasks[0], workspace.Path, suite.FixtureStager);

        grade.Passed.ShouldBe(expected, $"detail='{grade.Detail}' note='{grade.OracleNote}'");

        if (expected)
        {
            grade.OracleNote!.ShouldNotContain("TAMPER", Case.Sensitive, "the fixture's judge was left alone");
            grade.OracleNote.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive, "a shell judge's commands start in the graded tree, so even the fixture's own judge is not isolated");
        }
        else grade.OracleNote!.ShouldContain("TAMPER VOIDED", Case.Sensitive);
    }

    [Theory]
    [InlineData("chmod-runner")]     // an honest `chmod +x tests/run_tests.sh` before running it directly
    [InlineData("add-test")]         // an honest extra test the cell wrote beside the fixture's
    public async Task An_honest_cell_that_touches_its_judges_directory_without_changing_its_bytes_is_never_called_a_tamper(string scenario)
    {
        if (!await ToolsAvailableAsync("sh")) return;

        using var suite = LoadHiddenSuite("#!/bin/sh\n[ \"$(sh src/sum.sh 2 2)\" = 4 ]\n");
        using var workspace = new TempDirectory("cs-oracle-hidden-");
        suite.Suite.FixtureStager.Stage(suite.Reference, workspace.Path);

        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "src", "sum.sh"), "#!/bin/sh\necho $(($1 + $2))\n");
        var runner = Path.Combine(workspace.Path, "tests", "run_tests.sh");

        if (scenario == "chmod-runner") File.SetUnixFileMode(runner, File.GetUnixFileMode(runner) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        else await File.WriteAllTextAsync(Path.Combine(workspace.Path, "tests", "test_extra.sh"), "true\n");

        var grade = await GradeBenchmarkAsync(suite.Suite.Tasks[0], workspace.Path, suite.Suite.FixtureStager);

        grade.Passed.ShouldBeTrue($"detail='{grade.Detail}' note='{grade.OracleNote}'");
        grade.OracleNote!.ShouldNotContain("TAMPER", Case.Sensitive, "only a change to the fixture's own bytes is a tamper");

        if (scenario == "add-test") grade.OracleNote.ShouldContain("additions there were discarded: tests/test_extra.sh", Case.Sensitive, "the fixture owns its judge directory whole — an addition is discarded, said neutrally");
    }

    [Fact]
    public async Task A_cell_whose_subject_rewrites_the_fixtures_judge_mid_grade_loses_the_pass()
    {
        if (!await ToolsAvailableAsync("sh", "cmp")) return;

        using var suite = LoadHiddenSuite("#!/bin/sh\nsh src/sum.sh 2 2 > out.txt\ncmp -s out.txt tests/expected.txt\n", ("tests/expected.txt", "4\n"));
        using var workspace = new TempDirectory("cs-oracle-hidden-");
        suite.Suite.FixtureStager.Stage(suite.Reference, workspace.Path);

        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "src", "sum.sh"), "#!/bin/sh\nrm -f tests/expected.txt; echo 0 > tests/expected.txt\necho 0\n");

        var grade = await GradeBenchmarkAsync(suite.Suite.Tasks[0], workspace.Path, suite.Suite.FixtureStager);

        grade.Passed.ShouldBeFalse($"detail='{grade.Detail}' note='{grade.OracleNote}'");
        grade.Detail.ShouldBe(OracleGuard.ChangedDuringCheckDetail);
        grade.OracleNote!.ShouldContain("tests/expected.txt");
    }

    [Fact]
    public async Task A_seed_cells_oracle_note_reaches_its_durable_benchmark_row_and_evidence()
    {
        // Every seed cell runs `sh check.sh` at the repository root, which sources the subject in-process, so it can
        // never grade clean. The label used to stop at the in-memory grade: the persisted row read Solved,
        // "tests-passed", with no trace of it.
        if (!await ToolsAvailableAsync("sh")) return;

        var task = SeedBenchmarkCorpus.Tasks.First(t => t.FixtureRef == "failing-assertion");
        using var workspace = new TempDirectory("cs-oracle-bench-");
        SeedBenchmarkFixtures.Stage(task.FixtureRef, workspace.Path);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "solution.sh"), "#!/bin/sh\nREPORTED_SUM=5\n");

        var grade = await GradeBenchmarkAsync(task, workspace.Path, new SeedFixtureStager());

        grade.Passed.ShouldBeTrue(grade.EvidenceText);
        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive);
        grade.EvidenceText!.ShouldStartWith(grade.OracleNote, Case.Sensitive, "a reader that keeps only the evidence still sees the label");

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var suiteVersion = "oracle-note-" + Guid.NewGuid().ToString("N");
        var result = new BenchmarkResult { TaskId = task.Id, Mode = BenchmarkMode.HarnessCli, RunStatus = AgentRunStatus.Succeeded, McpFullCatalog = false, Grade = grade, DurationSeconds = 1 };

        using (var scope = fixture.BeginScope())
            await scope.Resolve<IBenchmarkResultStore>().RecordAsync(teamId, suiteVersion, result, selection: null, CancellationToken.None);

        using var readScope = fixture.BeginScope();
        var row = await readScope.Resolve<CodeSpaceDbContext>().BenchmarkResultRecord.AsNoTracking().SingleAsync(r => r.TeamId == teamId && r.SuiteVersion == suiteVersion);

        row.Solved.ShouldBeTrue();
        row.OracleNote.ShouldBe(grade.OracleNote, "the row keeps the label beside the solve");
    }

    [Fact]
    public async Task A_launch_cell_patch_that_touches_the_judge_is_voided()
    {
        if (!await ToolsAvailableAsync("sh", "git")) return;

        var task = SeedBenchmarkCorpus.Tasks.First(t => t.FixtureRef == "failing-assertion");
        using var workspace = new TempDirectory("cs-oracle-launch-");
        SeedBenchmarkFixtures.Stage(task.FixtureRef, workspace.Path);
        await GitAsync(workspace.Path, "init", "-q", "-b", "main");
        await GitAsync(workspace.Path, "-c", "user.email=t@t.local", "-c", "user.name=t", "add", "-A");
        await GitAsync(workspace.Path, "-c", "user.email=t@t.local", "-c", "user.name=t", "commit", "-q", "-m", "fixture");

        var patch = await JudgePatchAsync(workspace.Path);

        using (var scope = fixture.BeginScope())
        {
            var cells = (TaskLaunchBenchmarkCellRunner)scope.Resolve<ITaskLaunchBenchmarkCellRunner>();
            await cells.ReconstructWorkspaceAsync(workspace.Path, new[] { Attempt(patch) }, CancellationToken.None);
        }

        var grade = await GradeBenchmarkAsync(task, workspace.Path, new SeedFixtureStager());

        grade.Passed.ShouldBeFalse($"the launch run's patch rewrote check.sh — the frozen fixture's judge decides (detail='{grade.Detail}')");
        grade.OracleNote!.ShouldContain("TAMPER VOIDED", Case.Sensitive);
    }

    // ── Chassis ──────────────────────────────────────────────────────────────────────

    private Task<BenchmarkGrade> GradeFloorAsync(Repo repo, IReadOnlyList<string> command, bool patchLane = false) => GradeSpecAsync(repo, new SupervisorAcceptanceSpec { Command = command }, patchLane);

    /// <summary>Grade <paramref name="spec"/> as the run's OPERATOR FLOOR: its own program files are the run's oracle inventory, exactly the anchor production builds.</summary>
    private async Task<BenchmarkGrade> GradeSpecAsync(Repo repo, SupervisorAcceptanceSpec spec, bool patchLane)
    {
        var floor = AcceptanceOracleProtection.ProgramCandidates(spec.Command);
        using var scope = fixture.BeginScope();
        var grader = scope.Resolve<ISupervisorAcceptanceGrader>();

        if (patchLane) return await grader.GradePatchAsync(new PatchAcceptanceGradeRequest { RepositoryId = repo.RepositoryId, TeamId = repo.TeamId, BaseSha = repo.BaseSha, InlinePatch = repo.Patch, PatchArtifactId = null, Spec = spec, TimeoutSeconds = 60, OracleFloorPrograms = floor, Posture = null }, CancellationToken.None);

        return await grader.GradeAsync(new RepositoryAcceptanceGradeRequest { RepositoryId = repo.RepositoryId, TeamId = repo.TeamId, Branch = "candidate", Spec = spec, TimeoutSeconds = 60, Anchor = new OracleAnchor(repo.BaseSha, floor), Posture = null }, CancellationToken.None);
    }

    private async Task<BenchmarkGrade> GradeBenchmarkAsync(BenchmarkTask task, string workspaceDirectory, IBenchmarkFixtureStager stager)
    {
        using var scope = fixture.BeginScope();

        return await BenchmarkTaskGrading.GradeAsync(scope.Resolve<IBenchmarkGraderRegistry>(), scope.Resolve<ISandboxRunnerRegistry>(), new BenchmarkTaskGradingRequest { Task = task, WorkspaceDirectory = workspaceDirectory, Posture = null, FixtureStager = stager }, CancellationToken.None);
    }

    private async Task<Repo> RepoAsync(Dictionary<string, string> baseFiles, Dictionary<string, string> candidateFiles, IReadOnlyList<string>? executables = null)
    {
        var teamId = (await WorkflowsTestSeed.SeedTeamAsync(fixture)).TeamId;
        var remote = new BareRemote();

        try
        {
            await remote.SeedBaseAsync(baseFiles);
            var baseSha = await remote.HeadShaAsync();
            await remote.CommitOnBranchAsync("candidate", candidateFiles, executables ?? Array.Empty<string>());

            return new Repo(remote, teamId, await SeedBoundRepositoryAsync(teamId, remote.Url), baseSha, await remote.DiffAsync(baseSha));
        }
        catch
        {
            remote.Dispose();
            throw;
        }
    }

    private sealed record Repo(BareRemote Remote, Guid TeamId, Guid RepositoryId, string BaseSha, string Patch) : IDisposable
    {
        public void Dispose() => Remote.Dispose();
    }

    private async Task<ScratchSeed> SeedScratchAsync(IReadOnlyList<string> argv)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var directory = Path.Combine(Path.GetTempPath(), "cs-oracle-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var task = new AgentTask { Goal = "write the answer", Harness = "test", WorkspaceDirectory = directory, Autonomy = AgentAutonomyLevel.Trusted, Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted), Acceptance = new SupervisorAcceptanceSpec { Command = argv, TimeoutSeconds = 30 } };
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var runs = scope.Resolve<IAgentRunService>();
        var run = await runs.CreateAsync(task, teamId, null, null, cancellationToken: CancellationToken.None);
        var owner = await runs.ClaimOwnershipAsync(run.Id, CancellationToken.None);

        return new ScratchSeed(teamId, directory, task, owner.ShouldNotBeNull());
    }

    private sealed record ScratchSeed(Guid TeamId, string Directory, AgentTask Task, AgentRunOwnerToken Owner) : IDisposable
    {
        public LocalAcceptancePreparation Preparation => new(Owner, TeamId, Task, SandboxKinds.Local, Directory);

        public LocalAcceptanceRequest Request(LocalAcceptanceContext? context) => new(Owner, TeamId, Task, context);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best-effort */ }
        }
    }

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "oracle-isolation-token" })),
            Status = CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    /// <summary>A real patch that rewrites the seed fixture's check.sh — captured off a clone with <c>git diff</c>, never hand-typed, exactly like a launch attempt's recorded <c>AgentRunResult.Patch</c>.</summary>
    private static async Task<string> JudgePatchAsync(string repoDirectory)
    {
        using var clone = new TempDirectory("cs-oracle-launch-src-");
        await GitAsync(Path.GetTempPath(), "clone", "-q", repoDirectory, clone.Path);
        await File.WriteAllTextAsync(Path.Combine(clone.Path, "check.sh"), "#!/bin/sh\nexit 0\n");

        return await GitAsync(clone.Path, "diff");
    }

    private static AgentRun Attempt(string patch) => new()
    {
        Id = Guid.NewGuid(),
        Status = AgentRunStatus.Succeeded,
        ResultJson = JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = patch }, AgentJson.Options),
    };

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A hidden-suite-shaped fixture (judge in its own tests/ directory, subject in src/) loaded through the real loader.</summary>
    private static HiddenSuiteFixture LoadHiddenSuite(string runTests, params (string Path, string Content)[] extra)
    {
        var directory = new TempDirectory("cs-oracle-suite-");
        var reference = "development/" + Guid.NewGuid().ToString("N");
        var task = new BenchmarkTask
        {
            Id = "sum", Description = "a hidden-suite-shaped fixture whose judge runs the subject in a child process", FixtureRef = reference, Goal = "make sum.sh add its operands",
            Grading = BenchmarkGradingKind.TestsPass, Harness = "codex-cli", Modes = new[] { BenchmarkMode.HarnessCli }, TestCommand = new[] { "sh", "tests/run_tests.sh" }, TimeoutSeconds = 30,
        };
        WriteFile(directory.Path, $"fixtures/{reference}/tests/run_tests.sh", runTests);
        WriteFile(directory.Path, $"fixtures/{reference}/src/sum.sh", "#!/bin/sh\necho 0\n");
        foreach (var (path, content) in extra) WriteFile(directory.Path, $"fixtures/{reference}/{path}", content);
        WriteFile(directory.Path, "tasks.json", JsonSerializer.Serialize(new[] { task }, AgentJson.Options));

        return new HiddenSuiteFixture(directory, reference, HiddenSuiteLoader.Load(directory.Path));
    }

    private sealed record HiddenSuiteFixture(TempDirectory Directory, string Reference, HiddenSuite Suite) : IDisposable
    {
        public void Dispose() => Directory.Dispose();
    }

    private static async Task<string> CommandPathAsync(string tool)
    {
        var probe = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "sh", Args = new[] { "-c", $"command -v {tool}" }, TimeoutSeconds = 10 }, CancellationToken.None);

        return probe.Stdout.Trim();
    }

    private static async Task<bool> ToolsAvailableAsync(params string[] tools)
    {
        if (OperatingSystem.IsWindows()) return false;

        foreach (var tool in tools)
        {
            try
            {
                var probe = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "sh", Args = new[] { "-c", $"command -v {tool}" }, TimeoutSeconds = 10 }, CancellationToken.None);
                if (probe.Status != SandboxStatus.Success) return false;
            }
            catch { return false; }
        }

        return true;
    }

    private static async Task<string> GitAsync(string workdir, params string[] args)
    {
        var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

        if (result.Status != SandboxStatus.Success || result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");

        return result.Stdout;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-oracle-isolation-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;
        private readonly string _seed;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
            _seed = Path.Combine(_root, "seed");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedBaseAsync(Dictionary<string, string> files)
        {
            await GitAsync(_root, "init", "--bare", "-b", "main", _bare);

            Directory.CreateDirectory(_seed);
            await GitAsync(_seed, "clone", _bare, _seed);
            await GitAsync(_seed, "config", "user.email", "test@codespace.dev");
            await GitAsync(_seed, "config", "user.name", "Test");
            await GitAsync(_seed, "config", "commit.gpgsign", "false");

            Write(files, Array.Empty<string>());
            await GitAsync(_seed, "add", "-A");
            await GitAsync(_seed, "commit", "-m", "seed");
            await GitAsync(_seed, "push", "origin", "main");
        }

        public async Task<string> HeadShaAsync() => (await GitAsync(_seed, "rev-parse", "HEAD")).Trim();

        public async Task<string> DiffAsync(string baseSha) => await GitAsync(_seed, "diff", baseSha, "HEAD");

        public async Task CommitOnBranchAsync(string branch, Dictionary<string, string> files, IReadOnlyList<string> executables)
        {
            await GitAsync(_seed, "checkout", "-b", branch);

            Write(files, executables);
            await GitAsync(_seed, "add", "-A");
            await GitAsync(_seed, "commit", "-m", "candidate work");
            await GitAsync(_seed, "push", "origin", branch);
        }

        private void Write(Dictionary<string, string> files, IReadOnlyList<string> executables)
        {
            foreach (var (name, content) in files)
            {
                WriteFile(_seed, name, content);

                if (executables.Contains(name)) File.SetUnixFileMode(Path.Combine(_seed, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
