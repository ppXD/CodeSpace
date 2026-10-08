using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// The tree a benchmark cell is graded in. It is a copy of what the cell produced, made OUTSIDE the agent's workspace,
/// in which every judge the task's test command runs has been put back from the FROZEN FIXTURE before the check runs.
///
/// <para>The fixture ships its own judge (a seed <c>check.sh</c>, a hidden suite's <c>tests/run_tests.sh</c>), and both
/// benchmark instruments used to grade the very tree the evaluated agent wrote. Editing the judge therefore graded any
/// cell solved, and those receipts ranked the brain. Grading reads the judge from bytes the agent could not author:
/// each judge's scope (<see cref="AcceptanceOracleProtection.JudgeScope"/>) is compared with the fixture, restored
/// from it and sealed. The fixture owns that whole scope, so nothing the cell put there survives, but only a change to
/// bytes the fixture holds is a TAMPER: a cell that edited or deleted its judge is FLAGGED and the tamper is void, an
/// addition is discarded and said so neutrally, and a mode change (an honest <c>chmod +x</c> before running the judge
/// directly) is put back silently. The rest of the cell's work is still graded by the fixture's own judge.</para>
///
/// <para>The scope is fingerprinted once sealed and again after the check (<see cref="OracleGuard.AfterCheck"/>): a
/// subject that rewrote the fixture's judge while it ran voids its pass.</para>
///
/// <para>With no stager (an ad-hoc cell staged by hand), nothing is restored, and the grade's runtime note says the judge
/// was not platform-owned.</para>
/// </summary>
internal sealed class BenchmarkOracleWorld : IDisposable
{
    private readonly string? _fixtureDirectory;

    private BenchmarkOracleWorld(string directory, string? fixtureDirectory)
    {
        Directory = directory;
        _fixtureDirectory = fixtureDirectory;
    }

    /// <summary>The grading tree — the check's working directory.</summary>
    public string Directory { get; }

    /// <summary>The judge scopes restored from the frozen fixture, as the grade's pinned paths.</summary>
    public IReadOnlyList<string> PinnedPaths { get; private set; } = Array.Empty<string>();

    /// <summary>Every path inside a judge scope whose fixture bytes the cell changed or deleted.</summary>
    public IReadOnlyList<string> TamperedPaths { get; private set; } = Array.Empty<string>();

    /// <summary>Every path the cell added inside a judge scope, which the fixture never held.</summary>
    public IReadOnlyList<string> DiscardedPaths { get; private set; } = Array.Empty<string>();

    private OracleTree.ScopeFingerprint? _sealed;

    /// <summary>Build the grading tree for <paramref name="task"/> from <paramref name="workspaceDirectory"/>. Throws when the fixture cannot be re-staged — a cell graded over a judge we cannot vouch for is an infra fault, never a verdict.</summary>
    public static BenchmarkOracleWorld Prepare(BenchmarkTask task, string workspaceDirectory, IBenchmarkFixtureStager? stager)
    {
        var world = new BenchmarkOracleWorld(NewDirectory("grade-bench-"), stager is null ? null : NewDirectory("fixture-bench-"));

        try
        {
            OracleTree.CopyTree(workspaceDirectory, world.Directory);
            world.RestoreJudges(task, stager);
            return world;
        }
        catch
        {
            world.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The finished check's grade with what the world owes it: the scope compared again (<see cref="OracleGuard.AfterCheck"/>),
    /// then the restore's own account — a TAMPER line for changed fixture bytes and a neutral line for discarded additions,
    /// each one line on <c>OracleNote</c> and the full list at the head of the evidence. Unchanged when the judge was left alone.
    /// </summary>
    public BenchmarkGrade Conclude(BenchmarkGrade grade)
    {
        if (_sealed is { } before) grade = OracleGuard.AfterCheck(grade, before, OracleTree.Fingerprint(Directory, PinnedPaths), _ => true);

        grade = Account(grade, DiscardedPrefix, DiscardedPaths);

        return Account(grade, TamperNotePrefix, TamperedPaths);
    }

    private static BenchmarkGrade Account(BenchmarkGrade grade, string prefix, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return grade;

        return grade with
        {
            OracleNote = OracleRuntime.CombineNotes($"{prefix} {OracleGuard.Flatten(paths)}", grade.OracleNote),
            EvidenceText = $"{prefix}\n{string.Join('\n', paths)}\n{grade.EvidenceText}",
        };
    }

    private const string TamperNotePrefix = OracleGuard.TamperNoteMarker + " \u2014 the cell changed its judge, restored from the frozen fixture:";

    private const string DiscardedPrefix = "judge directory restored from the frozen fixture; the cell's additions there were discarded:";

    private void RestoreJudges(BenchmarkTask task, IBenchmarkFixtureStager? stager)
    {
        if (stager is null || _fixtureDirectory is null) return;

        stager.Stage(task.FixtureRef, _fixtureDirectory);

        var scopes = AcceptanceOracleProtection.ProgramCandidates(task.TestCommand).Where(p => File.Exists(Path.Combine(_fixtureDirectory, p))).Select(AcceptanceOracleProtection.JudgeScope).Distinct(StringComparer.Ordinal).ToList();

        var changes = scopes.SelectMany(scope => OracleTree.RestoreScope(_fixtureDirectory, Directory, scope)).ToList();

        TamperedPaths = PathsOf(changes, OracleTree.ScopeChangeKind.Changed);
        DiscardedPaths = PathsOf(changes, OracleTree.ScopeChangeKind.Added);

        OracleTree.Seal(Directory, scopes);

        PinnedPaths = scopes;
        _sealed = scopes.Count == 0 ? null : OracleTree.Fingerprint(Directory, scopes);
    }

    private static IReadOnlyList<string> PathsOf(IEnumerable<OracleTree.ScopeChange> changes, OracleTree.ScopeChangeKind kind) =>
        changes.Where(change => change.Kind == kind).Select(change => change.Path).Distinct(StringComparer.Ordinal).ToList();

    private static string NewDirectory(string prefix)
    {
        var path = Path.Combine(LocalGitWorkspaceProvider.WorkspacesRoot, prefix + Guid.NewGuid().ToString("N"));

        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        TryDelete(Directory);
        if (_fixtureDirectory is not null) TryDelete(_fixtureDirectory);
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true); } catch { /* best-effort — the workspace janitor reclaims an orphaned grading tree */ }
    }
}
