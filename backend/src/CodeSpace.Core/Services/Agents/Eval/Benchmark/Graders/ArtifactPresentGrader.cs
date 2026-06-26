using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;

/// <summary>
/// The DELIVERABLE-EXISTS oracle (<see cref="BenchmarkGradingKind.ArtifactPresent"/>): a deterministic,
/// agent-INDEPENDENT "definition of done" for NON-coding work (research / analysis / audit) whose output is a FILE,
/// not a passing test. The grading command is read as the list of repo-relative PATHS that must exist on the produced
/// branch's workspace; ALL present → solved, ANY missing → fail. Like <see cref="TestsPassGrader"/> the verdict is the
/// code's filesystem check on an agent-independent clone — never the model's opinion of itself, so it keeps the
/// system's strongest property (the score is not the agent's self-report) while extending acceptance past argv-exit-0.
///
/// <para>Fail-closed: no workspace, an EMPTY path list (a configured-but-unspecified gate), a blank path, the workspace
/// root itself, or a path that escapes the workspace all return a FAILED grade — never a silent pass. A <c>../</c>
/// escape, an absolute path, and a SYMLINK whose final target leaves the clone all read as missing (the existence check
/// is clamped to the produced workspace by both a lexical root-prefix test and a resolve-the-link-target re-clamp).</para>
/// </summary>
public sealed class ArtifactPresentGrader : IBenchmarkGrader, ISingletonDependency
{
    public BenchmarkGradingKind Kind => BenchmarkGradingKind.ArtifactPresent;

    public Task<BenchmarkGrade> GradeAsync(BenchmarkGradingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.WorkspaceDirectory))
            return Task.FromResult(Fail("no-workspace"));

        var paths = context.Task.TestCommand;   // the declared deliverable paths (reuses the ad-hoc command slot)

        if (paths.Count == 0)
            return Task.FromResult(Fail("no-artifact-paths"));   // configured-but-unspecified gate → fail-closed, never a silent pass

        var root = Path.GetFullPath(context.WorkspaceDirectory);

        foreach (var path in paths)
            if (!ExistsWithin(root, path))
                return Task.FromResult(Fail($"artifact-missing: {path}"));

        return Task.FromResult(new BenchmarkGrade { Passed = true, Detail = "artifacts-present" });
    }

    /// <summary>
    /// True when the repo-relative path resolves to an existing file or directory STRICTLY within the workspace root.
    /// Every way of NOT being a real in-clone deliverable reads as missing (fail-closed): a blank path; a <c>../</c>
    /// escape or absolute path (lexically clamped); the workspace root itself (<c>.</c> / <c>""</c> — the clone dir is
    /// never a deliverable, and it always exists, so admitting it would be a silent pass); and a SYMLINK whose final
    /// target leaves the clone. The last guard matters because <see cref="File.Exists(string)"/> /
    /// <see cref="Directory.Exists(string)"/> FOLLOW symlinks — a committed <c>report.md → /etc/passwd</c> spells
    /// in-bounds but its target is outside — so the link is resolved to its final target and re-clamped to root.
    /// </summary>
    private static bool ExistsWithin(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        var full = Path.GetFullPath(Path.Combine(root, relativePath));

        // STRICT containment: must live UNDER root — a ../ escape, an absolute path, OR the root dir itself all fail
        // (root never ends with the separator, so `root.StartsWith(root + sep)` is false → the root-self case is rejected).
        if (!IsStrictlyWithin(root, full)) return false;

        if (!File.Exists(full) && !Directory.Exists(full)) return false;

        // The lexical path exists + spells in-bounds, but the existence probe followed any symlink — resolve the link to
        // its FINAL target and re-clamp, so an in-clone symlink pointing OUT of the clone reads as missing (fail-closed).
        var info = Directory.Exists(full) ? (FileSystemInfo)new DirectoryInfo(full) : new FileInfo(full);
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);

        return resolved is null || IsStrictlyWithin(root, Path.GetFullPath(resolved.FullName));   // non-symlink → the in-bounds lexical path IS the real path
    }

    /// <summary>True when <paramref name="candidate"/> lives STRICTLY under <paramref name="root"/> (a proper descendant — not root itself, not an escape).</summary>
    private static bool IsStrictlyWithin(string root, string candidate) =>
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static BenchmarkGrade Fail(string detail) => new() { Passed = false, Detail = detail };
}
