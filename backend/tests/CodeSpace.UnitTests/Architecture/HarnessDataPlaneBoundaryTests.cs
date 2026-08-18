using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// The harness data-plane vocabulary is TYPES ONLY: it is the seam later slices attach writers, readers and schema
/// to, and none of those exist yet. Until they do, no production code may consume the types — a half-wired consumer
/// would record against a shape nothing persists, which is how a "lossless" plane quietly starts losing data.
///
/// <para>The scan covers EVERY production project except the one that declares the types, because a consumer that
/// lands in Api or Mcp crosses the same boundary a consumer in Core does; a guard that watched Core alone would
/// wave two of the three consuming assemblies through.</para>
///
/// <para>When the first capture slice legitimately lands a writer, DELETE this test in that PR. Failing here is the
/// intended signal that the types-only boundary is being crossed, so the crossing is reviewed rather than
/// discovered later.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class HarnessDataPlaneBoundaryTests
{
    private static readonly IReadOnlyList<string> DataPlaneTypeNames = new[]
    {
        nameof(HarnessDescriptor), nameof(NativeRecordV1), nameof(AgentSemanticEventV1), nameof(RunnerHandleEnvelope),
    };

    [Fact]
    public void No_production_source_file_consumes_the_harness_data_plane_types_yet()
    {
        var sources = ProductionSourceFiles();

        sources.Count.ShouldBeGreaterThan(100, "the backend/src production tree was not found, so this scan would pass vacuously");

        var offenders = sources
            .Where(ConsumesADataPlaneType)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty("these production files reference the harness data-plane types, which have no writer, reader or schema yet:\n  " + string.Join("\n  ", offenders));
    }

    private static bool ConsumesADataPlaneType(string file)
    {
        var source = File.ReadAllText(file);

        return DataPlaneTypeNames.Any(name => source.Contains(name, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ProductionSourceFiles()
    {
        var sourceRoot = Path.Combine(FindRepoRoot(), "backend", "src");
        var declaringProject = $"{Path.DirectorySeparatorChar}CodeSpace.Messages{Path.DirectorySeparatorChar}";

        if (!Directory.Exists(sourceRoot)) return Array.Empty<string>();

        return Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(declaringProject, StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "backend"))) directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
