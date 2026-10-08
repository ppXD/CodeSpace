using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// 🟢 Unit (real files, POSIX-only — Rule 12.1): what a grade concludes from comparing a judge's scope before and after
/// its check ran. The seal is only a speed bump — the check runs as the user that owns those files — so this comparison
/// is what keeps a subject that rewrites the judge mid-grade from buying a pass that reads as protected.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OracleGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-oracle-guard-" + Guid.NewGuid().ToString("N"));

    public OracleGuardTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "tests"));
        File.WriteAllText(Path.Combine(_root, "tests", "check.sh"), "judge\n");
        File.WriteAllText(Path.Combine(_root, "tests", "expected.txt"), "7\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static readonly BenchmarkGrade Pass = new() { Passed = true, Detail = "tests-passed", EvidenceText = "$ sh tests/check.sh\nexit=0" };

    [Fact]
    public void The_literals_readers_key_on_are_pinned()
    {
        // A rename silently unlabels every voided or flagged grade on durable rows and in the metrics that count them.
        OracleGuard.TamperNoteMarker.ShouldBe("ORACLE TAMPER VOIDED");
        OracleGuard.ChangedDuringCheckDetail.ShouldBe("tests-judge-changed-during-check");
    }

    [Theory]
    [InlineData("tests/conftest.py", true)]
    [InlineData("conftest.py", true)]
    [InlineData("tests/sitecustomize.py", true)]
    [InlineData("tests/usercustomize.py", true)]
    [InlineData("tests/evil.pth", true)]
    [InlineData("tests/helpers.py", false)]
    [InlineData("tests/expected.txt", false)]
    [InlineData("tests/conftest.py.bak", false)]
    public void Only_files_a_runtime_loads_by_their_presence_are_hooks(string path, bool expected) =>
        OracleGuard.IsAutoLoaded(path).ShouldBe(expected);

    [Fact]
    public void An_untouched_judge_leaves_the_grade_as_it_was()
    {
        if (OperatingSystem.IsWindows()) return;

        var before = Print();

        OracleGuard.AfterCheck(Pass, before, Print(), _ => true).ShouldBe(Pass);
    }

    [Theory]
    [InlineData("rewrite")]
    [InlineData("unlink-and-recreate")]
    public void A_pass_whose_platform_owned_judge_bytes_changed_during_the_check_is_voided(string how)
    {
        if (OperatingSystem.IsWindows()) return;

        var before = Print();
        var expected = Path.Combine(_root, "tests", "expected.txt");

        if (how == "rewrite") File.WriteAllText(expected, "6\n");
        else
        {
            File.Delete(expected);
            File.WriteAllText(expected, "6\n");
        }

        var grade = OracleGuard.AfterCheck(Pass, before, Print(), _ => true);

        grade.Passed.ShouldBeFalse("the verdict was decided against judge bytes the subject rewrote — it cannot stand");
        grade.Detail.ShouldBe(OracleGuard.ChangedDuringCheckDetail);
        grade.Class.ShouldBe(GradeFailureClass.Genuine, "the candidate's own code did it — a verdict on the work, never infra");
        grade.OracleNote!.ShouldStartWith(OracleGuard.TamperNoteMarker, Case.Sensitive);
        grade.OracleNote.ShouldContain("tests/expected.txt");
        grade.EvidenceText!.ShouldStartWith(OracleGuard.TamperNoteMarker, Case.Sensitive);
        grade.EvidenceText.ShouldContain("$ sh tests/check.sh", Case.Sensitive, "the check's own output is kept beneath the account");
    }

    [Fact]
    public void A_failing_grade_whose_judge_changed_keeps_its_failure_and_gains_the_account()
    {
        if (OperatingSystem.IsWindows()) return;

        var failed = new BenchmarkGrade { Passed = false, Detail = "tests-failed-exit-1", Class = GradeFailureClass.Genuine };
        var before = Print();
        File.WriteAllText(Path.Combine(_root, "tests", "expected.txt"), "6\n");

        var grade = OracleGuard.AfterCheck(failed, before, Print(), _ => true);

        grade.Detail.ShouldBe("tests-failed-exit-1");
        grade.OracleNote!.ShouldStartWith(OracleGuard.TamperNoteMarker, Case.Sensitive);
    }

    [Fact]
    public void A_change_to_bytes_the_candidate_already_owned_or_an_addition_is_labelled_not_voided()
    {
        if (OperatingSystem.IsWindows()) return;

        var before = Print();
        File.WriteAllText(Path.Combine(_root, "tests", "expected.txt"), "6\n");   // the candidate's own kept copy
        File.WriteAllText(Path.Combine(_root, "tests", "build.log"), "a judge writing beside itself");

        var grade = OracleGuard.AfterCheck(Pass, before, Print(), path => path == "tests/check.sh");

        grade.Passed.ShouldBeTrue("nothing the platform owns changed");
        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive, "but the judge's directory moved under the check, so the pass may not read clean");
        grade.OracleNote.ShouldContain("tests/build.log");
        grade.OracleNote.ShouldContain("tests/expected.txt");
    }

    [Fact]
    public void A_scope_too_large_to_compare_is_labelled_never_assumed_untouched()
    {
        if (OperatingSystem.IsWindows()) return;

        var grade = OracleGuard.AfterCheck(Pass, OracleTree.Fingerprint(_root, new[] { "tests/" }, maxEntries: 1), Print(), _ => true);

        grade.Passed.ShouldBeTrue();
        grade.OracleNote!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker, Case.Sensitive);
        grade.OracleNote.ShouldContain("too large");
    }

    private OracleTree.ScopeFingerprint Print() => OracleTree.Fingerprint(_root, new[] { "tests/" });
}
