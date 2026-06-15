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

    // ─── Helpers ───

    private async Task<BenchmarkGrade> GradeAsync(BenchmarkTask task) =>
        await new TestsPassGrader().GradeAsync(new BenchmarkGradingContext
        {
            Task = task,
            WorkspaceDirectory = _dir,
            Runner = new LocalProcessRunner(),
        }, CancellationToken.None);

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
