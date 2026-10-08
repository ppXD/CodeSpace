using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// Pins the objective grading oracle against a REAL <see cref="LocalProcessRunner"/> + a REAL workspace dir: the
/// grader re-runs the fixture's OWN test command and reads the exit code — never the agent's self-report. The
/// honesty property is the load-bearing assertion: a workspace whose check passes grades PASS, one whose check
/// fails grades FAIL, regardless of anything the "agent" claimed.
///
/// POSIX-only (Rule 12.1): the fixture check is a /bin/sh script the runner spawns.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TestsPassGraderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs-bench-grader-" + Guid.NewGuid().ToString("N"));

    public TestsPassGraderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Kind_is_tests_pass() => new TestsPassGrader().Kind.ShouldBe(BenchmarkGradingKind.TestsPass);

    [Theory]
    [InlineData(0, true, "tests-passed")]    // the fixture's check exits 0 → solved
    [InlineData(1, false, "tests-failed-exit-1")]   // it exits non-zero → not solved
    public async Task Grade_reads_the_fixture_test_commands_exit_code_as_ground_truth(int exitCode, bool expectedPass, string expectedDetail)
    {
        if (OperatingSystem.IsWindows()) return;

        StageCheckScript(exitCode);

        var grade = await GradeAsync(StageTask());

        grade.Passed.ShouldBe(expectedPass, "the verdict is the repo test command's exit code, not the agent's opinion");
        grade.Detail.ShouldBe(expectedDetail);
    }

    [Fact]
    public async Task A_run_with_no_workspace_is_ungradable_and_fails()
    {
        if (OperatingSystem.IsWindows()) return;

        var grade = await new TestsPassGrader().GradeAsync(new BenchmarkGradingContext
        {
            Task = StageTask(),
            WorkspaceDirectory = null,
            Runner = new LocalProcessRunner(),
        }, CancellationToken.None);

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldBe("no-workspace");
    }

    [Fact]
    public async Task A_task_with_no_test_command_is_ungradable_and_fails()
    {
        if (OperatingSystem.IsWindows()) return;

        var grade = await new TestsPassGrader().GradeAsync(new BenchmarkGradingContext
        {
            Task = StageTask() with { TestCommand = Array.Empty<string>() },
            WorkspaceDirectory = _dir,
            Runner = new LocalProcessRunner(),
        }, CancellationToken.None);

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldBe("no-test-command");
    }

    // ─── The isolated runtime, against a real interpreter ───

    private const string PlantedJson = "def load(f):\n    return {'answer': 7}\n";

    [Theory]
    [InlineData(false, false)]   // the candidate's json.py sits in the working directory — the stdlib's json still wins
    [InlineData(true, true)]     // the honest answer still passes
    public async Task An_inline_python_check_cannot_be_shadowed_by_a_module_in_the_graded_tree(bool honest, bool expected)
    {
        if (!PythonAvailable()) return;

        File.WriteAllText(Path.Combine(_dir, "out.json"), honest ? "{\"answer\": 7}" : "{\"answer\": 6}");
        File.WriteAllText(Path.Combine(_dir, "json.py"), PlantedJson);

        var grade = await GradeAsync(StageTask() with { TestCommand = new[] { "python3", "-c", "import json, sys\nsys.exit(0 if json.load(open('out.json'))['answer'] == 7 else 1)\n" } });

        grade.Passed.ShouldBe(expected, grade.EvidenceText);
        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive, "the tree's json.py and the stdlib both answer `import json`; which one the author meant cannot be known, so the grade says which one ran");
        grade.OracleNote.ShouldContain("json");
    }

    [Fact]
    public async Task An_inline_python_check_whose_imports_the_tree_does_not_shadow_reads_clean()
    {
        if (!PythonAvailable()) return;

        File.WriteAllText(Path.Combine(_dir, "out.json"), "{\"answer\": 7}");
        File.WriteAllText(Path.Combine(_dir, "statistics.py"), "def median(xs):\n    return xs[0]\n");   // present, but the check never imports it

        var grade = await GradeAsync(StageTask() with { TestCommand = new[] { "python3", "-c", "import json, sys\nsys.exit(0 if json.load(open('out.json'))['answer'] == 7 else 1)\n" } });

        grade.Passed.ShouldBeTrue(grade.EvidenceText);
        grade.OracleNote.ShouldBeNull("nothing the check imports has two candidates — a module it never imports is not a reason to label it");
    }

    [Theory]
    [InlineData("calendar.py", "def is_leap(y):\n    return y % 4 == 0 and (y % 100 != 0 or y % 400 == 0)\n", "import sys\nfrom calendar import is_leap\nsys.exit(0 if is_leap(2000) and not is_leap(1900) else 1)\n", false, "calendar")]   // honest work the stdlib name hides
    [InlineData("statistics.py", "def median(xs):\n    return xs[0]\n", "import sys\nfrom statistics import median\nsys.exit(0 if median([3, 1, 2]) == 2 else 1)\n", true, "statistics")]                                           // wrong work the stdlib answers for
    public async Task A_subject_module_named_like_the_stdlib_is_reported_never_graded_silently(string module, string source, string check, bool expectedPass, string named)
    {
        // The verdict here is of the interpreter's module, not the candidate's — in both directions. It cannot be
        // repaired without letting a planted module shadow the stdlib, so the grade must say which module it judged.
        if (!PythonAvailable()) return;

        File.WriteAllText(Path.Combine(_dir, module), source);

        var grade = await GradeAsync(StageTask() with { TestCommand = new[] { "python3", "-c", check } });

        grade.Passed.ShouldBe(expectedPass, grade.EvidenceText);
        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive);
        grade.OracleNote.ShouldContain(named);
        grade.OracleNote.ShouldContain("imported the interpreter's");
    }

    [Fact]
    public async Task The_grades_oracle_note_leads_its_evidence()
    {
        // A durable reader that keeps only the evidence (a benchmark row's CAS text, a receipt) must still see the label.
        if (OperatingSystem.IsWindows()) return;

        var grade = await GradeAsync(StageTask() with { TestCommand = new[] { "sh", "-c", "exit 0" } });

        grade.EvidenceText!.ShouldStartWith(grade.OracleNote!, Case.Sensitive);
    }

    [Fact]
    public async Task An_inline_python_check_still_imports_the_candidates_own_module()
    {
        // The hidden-suite shape: the oracle imports the SUBJECT it judges. Isolation moves the graded tree behind the
        // standard library; it must not drop it.
        if (!PythonAvailable()) return;

        File.WriteAllText(Path.Combine(_dir, "inventory.py"), "def summarize(rows):\n    return {'a': 1}\n");

        var grade = await GradeAsync(StageTask() with { TestCommand = new[] { "python3", "-c", "import sys\nfrom inventory import summarize\nsys.exit(0 if summarize([]) == {'a': 1} else 1)\n" } });

        grade.Passed.ShouldBeTrue(grade.EvidenceText);
    }

    [Fact]
    public async Task A_pinned_python_judge_imports_its_own_sibling_and_reads_the_candidates_data()
    {
        if (!PythonAvailable()) return;

        Directory.CreateDirectory(Path.Combine(_dir, "tests"));
        File.WriteAllText(Path.Combine(_dir, "tests", "helpers.py"), "EXPECTED = 7\n");
        File.WriteAllText(Path.Combine(_dir, "tests", "check.py"), "import sys\nfrom helpers import EXPECTED\nsys.exit(0 if int(open('answer.txt').read()) == EXPECTED else 1)\n");
        File.WriteAllText(Path.Combine(_dir, "answer.txt"), "7");
        File.WriteAllText(Path.Combine(_dir, "helpers.py"), "EXPECTED = 6\n");   // a same-named module in the graded tree never wins over the judge's own

        var grade = await GradeAsync(StageTask() with { TestCommand = new[] { "python3", "tests/check.py" } }, "tests/");

        grade.Passed.ShouldBeTrue(grade.EvidenceText);
        grade.OracleNote.ShouldBeNull();
    }

    [Fact]
    public async Task A_root_level_python_judge_reads_the_stdlib_before_the_tree_and_is_labelled()
    {
        if (!PythonAvailable()) return;

        File.WriteAllText(Path.Combine(_dir, "check.py"), "import json, sys\nfrom solution import ANSWER\nsys.exit(0 if json.loads('{\"a\": 1}') == {'a': 1} and ANSWER == 7 else 1)\n");
        File.WriteAllText(Path.Combine(_dir, "solution.py"), "ANSWER = 7\n");
        File.WriteAllText(Path.Combine(_dir, "json.py"), "def loads(s):\n    return None\n");

        var grade = await GradeAsync(StageTask() with { TestCommand = new[] { "python3", "check.py" } }, "check.py");

        grade.Passed.ShouldBeTrue($"the planted json.py lost to the stdlib, and the subject module still imported: {grade.EvidenceText}");
        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive, "only the file is the platform's — everything beside it is the candidate's");
        grade.OracleNote.ShouldContain("repository root");
        grade.OracleNote.ShouldContain("json in the graded tree", Case.Sensitive, "the judge imports json and the tree beside it supplies one — the grade names it");
    }

    [Fact]
    public async Task A_check_that_cannot_run_isolated_says_so_on_the_grade()
    {
        if (OperatingSystem.IsWindows()) return;

        var grade = await GradeAsync(StageTask() with { TestCommand = new[] { "sh", "-c", "exit 0" } });

        grade.Passed.ShouldBeTrue();
        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive);
    }

    // ─── Helpers ───

    private async Task<BenchmarkGrade> GradeAsync(BenchmarkTask task, params string[] pinned) =>
        await new TestsPassGrader().GradeAsync(new BenchmarkGradingContext
        {
            Task = task,
            WorkspaceDirectory = _dir,
            Runner = new LocalProcessRunner(),
            PinnedOraclePaths = pinned,
        }, CancellationToken.None);

    private static bool PythonAvailable()
    {
        if (OperatingSystem.IsWindows()) return false;

        try
        {
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python3", "--version") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            probe.WaitForExit();
            return probe.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private void StageCheckScript(int exitCode)
    {
        var script = Path.Combine(_dir, "check.sh");
        File.WriteAllText(script, $"#!/bin/sh\nexit {exitCode}\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private static BenchmarkTask StageTask() => new()
    {
        Id = "grader-test",
        Description = "exercise the tests-pass oracle",
        FixtureRef = "inline",
        Goal = "make the check pass",
        Grading = BenchmarkGradingKind.TestsPass,
        TestCommand = new[] { "sh", "check.sh" },
        Harness = "codex-cli",
        Modes = new[] { BenchmarkMode.HarnessCli },
        TimeoutSeconds = 30,
    };
}
