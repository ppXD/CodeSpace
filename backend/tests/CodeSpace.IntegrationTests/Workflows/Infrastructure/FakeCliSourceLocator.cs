namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Shared by every convention test that polices the fake-CLI family: where their sources live, and how to find one
/// by type name. Extracted from <c>FakeAgentCliHarnessConventionTests</c> (#1781) so "locate a fake's source across
/// both test assemblies, or say which one is unlocatable" has exactly one implementation rather than drifting
/// between siblings that each grew their own copy.
/// </summary>
public static class FakeCliSourceLocator
{
    /// <summary>Both test assemblies a fake CLI can live in, searched recursively — no fake is exempt by folder.</summary>
    public static IEnumerable<DirectoryInfo> TestSourceRoots() =>
        new[] { "backend/tests/CodeSpace.E2ETests", "backend/tests/CodeSpace.IntegrationTests" }
            .Select(rel => new DirectoryInfo(Path.Combine(FindRepoRoot(), rel)))
            .Where(d => d.Exists);

    /// <summary>True for build output (bin/obj) that must never be treated as a fake's source, even when it holds a stale copy of the .cs file.</summary>
    public static bool IsBuildOutput(FileInfo file) =>
        file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
     || file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>The source of a fake by type name, searched across BOTH test assemblies (one file per type, named after it — the repo's convention). Null ONLY when no such file exists anywhere, which callers RED on rather than treating as "nothing to police".</summary>
    public static string? SourceFor(string typeName) =>
        TestSourceRoots()
            .SelectMany(d => d.GetFiles(typeName + ".cs", SearchOption.AllDirectories))
            .Where(f => !IsBuildOutput(f))
            .Select(f => File.ReadAllText(f.FullName))
            .FirstOrDefault();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
