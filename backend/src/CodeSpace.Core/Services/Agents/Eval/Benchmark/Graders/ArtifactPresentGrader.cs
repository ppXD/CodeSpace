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
            if (!WorkspaceArtifactGuard.ExistsWithin(root, path))
                return Task.FromResult(Fail($"artifact-missing: {path}"));

        return Task.FromResult(new BenchmarkGrade { Passed = true, Detail = "artifacts-present" });
    }

    private static BenchmarkGrade Fail(string detail) => new() { Passed = false, Detail = detail };
}
