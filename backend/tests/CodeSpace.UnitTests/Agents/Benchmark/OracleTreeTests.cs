using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// 🟢 Unit (real files, POSIX-only — Rule 12.1): building a grading tree from a candidate's tree and putting a judge's
/// scope back from platform-owned bytes. The tree it reads is the candidate's, so the cases that matter are the hostile
/// ones: a link where a judge directory should be, an addition beside the judge, a FIFO that would hang a reader.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OracleTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-oracle-tree-" + Guid.NewGuid().ToString("N"));

    public OracleTreeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void A_copy_keeps_modes_and_copies_links_as_links()
    {
        if (OperatingSystem.IsWindows()) return;

        var source = Dir("source");
        Write(source, "tests/check.sh", "#!/bin/sh\nexit 0\n", executable: true);
        File.CreateSymbolicLink(Path.Combine(source, "outside"), "/etc/hosts");

        OracleTree.CopyTree(source, Path.Combine(_root, "copy"));

        File.GetUnixFileMode(Path.Combine(_root, "copy", "tests", "check.sh")).HasFlag(UnixFileMode.UserExecute).ShouldBeTrue("a judge copied without its exec bit would fail an honest cell");
        new FileInfo(Path.Combine(_root, "copy", "outside")).LinkTarget.ShouldBe("/etc/hosts", "a link is never followed into the host");
    }

    [Fact]
    public void A_fifo_in_the_candidates_tree_never_hangs_the_copy_or_the_compare()
    {
        if (OperatingSystem.IsWindows()) return;

        var platform = Dir("platform");
        var grading = Dir("grading");
        Write(platform, "tests/check.sh", "exit 1\n");
        Directory.CreateDirectory(Path.Combine(grading, "tests"));
        System.Diagnostics.Process.Start("mkfifo", Path.Combine(grading, "tests", "check.sh"))!.WaitForExit();

        OracleTree.CopyTree(grading, Path.Combine(_root, "copy"));
        var differing = OracleTree.RestoreScope(platform, grading, "tests/");

        differing.ShouldBe(new[] { Changed("tests/check.sh") });
        File.ReadAllText(Path.Combine(grading, "tests", "check.sh")).ShouldBe("exit 1\n");
    }

    [Fact]
    public void Restoring_a_scope_reverts_edits_removes_additions_restores_deletions_and_classifies_each()
    {
        if (OperatingSystem.IsWindows()) return;

        var platform = Dir("platform");
        Write(platform, "tests/check.sh", "judge\n");
        Write(platform, "tests/helpers.sh", "helpers\n");
        Write(platform, "tests/data/expected.txt", "7\n");
        var grading = Dir("grading");
        Write(grading, "tests/check.sh", "exit 0\n");
        Write(grading, "tests/data/expected.txt", "7\n");
        Write(grading, "tests/conftest.py", "planted\n");
        Write(grading, "solution.sh", "the work\n");

        var differing = OracleTree.RestoreScope(platform, grading, "tests/");

        differing.ShouldBe(new[] { Changed("tests/check.sh"), Added("tests/conftest.py"), Changed("tests/helpers.sh") }, "an edit and a deletion change platform bytes; an addition is only discarded");
        File.ReadAllText(Path.Combine(grading, "tests", "check.sh")).ShouldBe("judge\n");
        File.ReadAllText(Path.Combine(grading, "tests", "helpers.sh")).ShouldBe("helpers\n");
        File.Exists(Path.Combine(grading, "tests", "conftest.py")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(grading, "solution.sh")).ShouldBe("the work\n", "outside the scope is the candidate's work — never touched");
    }

    [Fact]
    public void An_untouched_scope_reports_nothing()
    {
        if (OperatingSystem.IsWindows()) return;

        var platform = Dir("platform");
        Write(platform, "check.sh", "judge\n", executable: true);
        var grading = Dir("grading");
        Write(grading, "check.sh", "judge\n", executable: true);

        OracleTree.RestoreScope(platform, grading, "check.sh").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, false)]    // a dropped exec bit
    [InlineData(false, true)]    // an added one — an honest `chmod +x tests/run_tests.sh` before running it directly
    public void A_mode_only_difference_is_restored_but_is_not_a_change_to_the_platforms_bytes(bool platformExecutable, bool gradingExecutable)
    {
        if (OperatingSystem.IsWindows()) return;

        var platform = Dir("platform");
        Write(platform, "check.sh", "judge\n", executable: platformExecutable);
        var grading = Dir("grading");
        Write(grading, "check.sh", "judge\n", executable: gradingExecutable);

        OracleTree.RestoreScope(platform, grading, "check.sh").ShouldBe(new[] { new OracleTree.ScopeChange("check.sh", OracleTree.ScopeChangeKind.ModeOnly) });
        File.GetUnixFileMode(Path.Combine(grading, "check.sh")).HasFlag(UnixFileMode.UserExecute).ShouldBe(platformExecutable, "the platform's mode is what the judge runs with");
    }

    [Fact]
    public void A_link_standing_where_the_judges_directory_should_be_is_replaced_never_written_through()
    {
        if (OperatingSystem.IsWindows()) return;

        var platform = Dir("platform");
        Write(platform, "tests/check.sh", "judge\n");
        var elsewhere = Dir("elsewhere");
        var grading = Dir("grading");
        Directory.CreateSymbolicLink(Path.Combine(grading, "tests"), elsewhere);

        OracleTree.RestoreScope(platform, grading, "tests/").ShouldBe(new[] { Added("tests"), Changed("tests/check.sh") });

        new DirectoryInfo(Path.Combine(grading, "tests")).LinkTarget.ShouldBeNull("the link is gone; a real directory holds the judge");
        File.ReadAllText(Path.Combine(grading, "tests", "check.sh")).ShouldBe("judge\n");
        Directory.EnumerateFileSystemEntries(elsewhere).ShouldBeEmpty("nothing was written through the link to where it pointed");
    }

    [Fact]
    public void A_fingerprint_sees_content_link_and_presence_changes_but_not_modes()
    {
        if (OperatingSystem.IsWindows()) return;

        var grading = Dir("grading");
        Write(grading, "tests/expected.txt", "7\n");
        Write(grading, "tests/check.sh", "judge\n", executable: true);
        Write(grading, "tests/gone.sh", "x\n");
        File.CreateSymbolicLink(Path.Combine(grading, "tests", "link"), "expected.txt");
        Write(grading, "solution.sh", "the work\n");

        var before = OracleTree.Fingerprint(grading, new[] { "tests/" });

        File.SetUnixFileMode(Path.Combine(grading, "tests", "check.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite);   // a mode alone is not content
        File.WriteAllText(Path.Combine(grading, "tests", "expected.txt"), "6\n");
        File.Delete(Path.Combine(grading, "tests", "gone.sh"));
        File.Delete(Path.Combine(grading, "tests", "link"));
        File.CreateSymbolicLink(Path.Combine(grading, "tests", "link"), "/etc/hosts");
        Write(grading, "tests/new.py", "planted\n");
        Write(grading, "solution.sh", "changed work\n");                                                                       // outside the scope

        var changes = before.Compare(OracleTree.Fingerprint(grading, new[] { "tests/" }));

        changes.Changed.ShouldBe(new[] { "tests/expected.txt", "tests/gone.sh", "tests/link" });
        changes.Added.ShouldBe(new[] { "tests/new.py" });
        before.Complete.ShouldBeTrue();
    }

    [Fact]
    public void A_fingerprint_over_its_budget_says_it_is_incomplete_rather_than_guessing()
    {
        if (OperatingSystem.IsWindows()) return;

        var grading = Dir("grading");
        for (var i = 0; i < 5; i++) Write(grading, $"tests/f{i}.txt", "x\n");

        OracleTree.Fingerprint(grading, new[] { "tests/" }, maxEntries: 3).Complete.ShouldBeFalse();
        OracleTree.Fingerprint(grading, new[] { "tests/" }, maxEntries: 5).Complete.ShouldBeTrue();
    }

    [Fact]
    public void A_fingerprint_of_a_scope_behind_a_link_records_the_link_and_never_walks_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var elsewhere = Dir("elsewhere");
        Write(elsewhere, "check.sh", "outside\n");
        var grading = Dir("grading");
        Directory.CreateSymbolicLink(Path.Combine(grading, "tests"), elsewhere);

        var print = OracleTree.Fingerprint(grading, new[] { "tests/" });

        print.Paths.ShouldBe(new[] { "tests" }, "a link where the judge's directory should be is one entry, never a walk into what it points at");
    }

    [Fact]
    public void Sealing_makes_the_judge_read_only_but_leaves_its_directory_writable_and_the_tree_deletable()
    {
        if (OperatingSystem.IsWindows()) return;

        var grading = Dir("grading");
        Write(grading, "tests/check.sh", "judge\n", executable: true);
        Write(grading, "solution.sh", "the work\n");

        OracleTree.Seal(grading, new[] { "tests/", "absent/" });

        var mode = File.GetUnixFileMode(Path.Combine(grading, "tests", "check.sh"));
        (mode & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)).ShouldBe((UnixFileMode)0, "a speed bump against an accidental rewrite — the comparison after the check is what voids a deliberate one");
        mode.HasFlag(UnixFileMode.UserExecute).ShouldBeTrue();
        File.GetUnixFileMode(Path.Combine(grading, "solution.sh")).HasFlag(UnixFileMode.UserWrite).ShouldBeTrue("outside the scope nothing changes");
        File.WriteAllText(Path.Combine(grading, "tests", "build.log"), "a judge may still write beside itself");

        Directory.Delete(grading, recursive: true);
        Directory.Exists(grading).ShouldBeFalse("a sealed grading tree is still discarded normally");
    }

    [Fact]
    public void A_copy_into_its_own_source_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;

        var source = Dir("source");

        Should.Throw<InvalidOperationException>(() => OracleTree.CopyTree(source, Path.Combine(source, "nested")));
    }

    private static OracleTree.ScopeChange Changed(string path) => new(path, OracleTree.ScopeChangeKind.Changed);

    private static OracleTree.ScopeChange Added(string path) => new(path, OracleTree.ScopeChangeKind.Added);

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string root, string relative, string content, bool executable = false)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead | (executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0));
    }
}
