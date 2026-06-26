using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// Pins the DELIVERABLE-EXISTS oracle (<see cref="ArtifactPresentGrader"/>) against a REAL workspace dir: the grading
/// command is read as the list of repo-relative paths that must exist, and the verdict is the code's filesystem check
/// on the produced workspace — never the agent's self-report. The honesty property is the same as the tests-pass
/// oracle's (the score is not the agent's opinion); this oracle just extends "done" past argv-exit-0 to "the declared
/// files are there", the definition of done for NON-coding (research / analysis) output.
///
/// <para>Fail-closed is the load-bearing guard: every way the check CANNOT reach a clean verdict — no workspace, an
/// empty path list (configured-but-unspecified), a blank path, a <c>../</c> escape — grades FAIL, never a silent
/// pass. The grader ignores the runner (it does no process spawn), so these run cross-platform with no OS guard.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactPresentGraderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs-artifact-grader-" + Guid.NewGuid().ToString("N"));

    public ArtifactPresentGraderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Kind_is_artifact_present() => new ArtifactPresentGrader().Kind.ShouldBe(BenchmarkGradingKind.ArtifactPresent);

    [Fact]
    public async Task All_declared_artifacts_present_grades_passed()
    {
        File.WriteAllText(Path.Combine(_dir, "report.md"), "# findings");
        Directory.CreateDirectory(Path.Combine(_dir, "data"));

        var grade = await GradeAsync("report.md", "data");   // a file AND a directory both count

        grade.Passed.ShouldBeTrue("every declared deliverable exists in the produced workspace");
        grade.Detail.ShouldBe("artifacts-present");
    }

    [Fact]
    public async Task A_single_missing_artifact_grades_fail_naming_the_path()
    {
        File.WriteAllText(Path.Combine(_dir, "report.md"), "# findings");

        var grade = await GradeAsync("report.md", "summary.md");   // second one was never produced

        grade.Passed.ShouldBeFalse("one missing deliverable means the work is not done");
        grade.Detail.ShouldBe("artifact-missing: summary.md");
    }

    [Fact]
    public async Task No_workspace_is_ungradable_and_fails_closed()
    {
        var grade = await new ArtifactPresentGrader().GradeAsync(new BenchmarkGradingContext
        {
            Task = StageTask("report.md"),
            WorkspaceDirectory = null,
            Runner = new LocalProcessRunner(),
        }, CancellationToken.None);

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldBe("no-workspace");
    }

    [Fact]
    public async Task An_empty_path_list_fails_closed_never_a_silent_pass()
    {
        var grade = await GradeAsync();   // configured-but-unspecified gate

        grade.Passed.ShouldBeFalse("an acceptance check with no declared artifacts cannot be 'satisfied' — it is unspecified, which fails closed");
        grade.Detail.ShouldBe("no-artifact-paths");
    }

    [Fact]
    public async Task A_blank_path_fails_closed()
    {
        File.WriteAllText(Path.Combine(_dir, "report.md"), "# findings");

        var grade = await GradeAsync("report.md", "   ");   // whitespace path can never resolve to a real deliverable

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldBe("artifact-missing:    ");
    }

    [Fact]
    public async Task A_path_naming_the_workspace_root_itself_reads_as_missing_never_a_silent_pass()
    {
        // "." / "" / a trailing-separator form all resolve to the workspace root, which ALWAYS exists — admitting it
        // would let the model satisfy the gate with no produced deliverable. It must fail-closed like every other
        // degenerate input.
        var grade = await GradeAsync(".");

        grade.Passed.ShouldBeFalse("the clone directory is never a deliverable — a path resolving to the root itself fails closed");
        grade.Detail.ShouldBe("artifact-missing: .");
    }

    [Fact]
    public async Task A_symlink_whose_target_leaves_the_workspace_reads_as_missing()
    {
        if (OperatingSystem.IsWindows()) return;   // symlink creation needs privilege on Windows; the guard is cross-platform but the fixture is POSIX

        // A symlink committed into the produced branch (lexically in-bounds: <root>/leak.md) whose target lives OUTSIDE
        // the clone. File.Exists FOLLOWS the link, so a purely-lexical guard would grade it PASS — the resolve-and-reclamp
        // guard catches it. (The deepest trust property: the existence check never reaches outside the produced clone.)
        var outsideName = "cs-symlink-target-" + Guid.NewGuid().ToString("N") + ".txt";
        var outside = Path.Combine(Path.GetTempPath(), outsideName);
        File.WriteAllText(outside, "secret");

        try
        {
            File.CreateSymbolicLink(Path.Combine(_dir, "leak.md"), outside);

            var grade = await GradeAsync("leak.md");

            grade.Passed.ShouldBeFalse("the symlink's final target lives outside the clone → it reads as missing, never reaches the real file");
            grade.Detail.ShouldBe("artifact-missing: leak.md");
        }
        finally
        {
            try { File.Delete(outside); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task A_symlink_whose_target_stays_inside_the_workspace_reads_as_present()
    {
        if (OperatingSystem.IsWindows()) return;

        // The legitimate case: an in-clone symlink pointing at a sibling that is itself in-clone must still count as
        // present — the guard rejects only symlinks whose final target ESCAPES, not symlinks per se.
        File.WriteAllText(Path.Combine(_dir, "real.md"), "# findings");
        File.CreateSymbolicLink(Path.Combine(_dir, "alias.md"), Path.Combine(_dir, "real.md"));

        var grade = await GradeAsync("alias.md");

        grade.Passed.ShouldBeTrue("a symlink whose final target stays inside the clone is a real in-clone deliverable");
        grade.Detail.ShouldBe("artifacts-present");
    }

    [Fact]
    public async Task A_parent_escape_path_reads_as_missing_even_when_the_target_exists_outside_the_workspace()
    {
        // A real file OUTSIDE the workspace, addressed via `../` — the check must never reach outside the produced clone,
        // so an existing escape target still grades missing (the existence check is clamped to the workspace root).
        var outsideName = "cs-outside-" + Guid.NewGuid().ToString("N") + ".txt";
        var outside = Path.Combine(Path.GetTempPath(), outsideName);
        File.WriteAllText(outside, "secret");

        try
        {
            var grade = await GradeAsync("../" + outsideName);

            grade.Passed.ShouldBeFalse("a ../ path resolves outside the workspace root and must read as missing, never reach the real file");
            grade.Detail.ShouldBe("artifact-missing: ../" + outsideName);
        }
        finally
        {
            try { File.Delete(outside); } catch { /* best-effort */ }
        }
    }

    // ─── Helpers ───

    private async Task<BenchmarkGrade> GradeAsync(params string[] artifactPaths) =>
        await new ArtifactPresentGrader().GradeAsync(new BenchmarkGradingContext
        {
            Task = StageTask(artifactPaths),
            WorkspaceDirectory = _dir,
            Runner = new LocalProcessRunner(),
        }, CancellationToken.None);

    private static BenchmarkTask StageTask(params string[] artifactPaths) => new()
    {
        Id = "artifact-grader-test",
        Description = "exercise the artifact-present oracle",
        FixtureRef = "inline",
        Goal = "produce the declared deliverables",
        Grading = BenchmarkGradingKind.ArtifactPresent,
        TestCommand = artifactPaths,   // for this oracle the command slot carries repo-relative deliverable paths
        Harness = "codex-cli",
        Modes = new[] { BenchmarkMode.HarnessCli },
        TimeoutSeconds = 30,
    };
}
