using System.Runtime.CompilerServices;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Resolves the committed <c>Cassettes/</c> directory in the SOURCE tree (not the build output) so RECORD
/// writes a transcript a human can <c>git add</c>, and REPLAY reads from the same committed file. Anchored via
/// <see cref="CallerFilePathAttribute"/> on this file's own location — robust to the working directory and
/// independent of any csproj content-copy rule.
/// </summary>
public static class RealModelCassettePaths
{
    /// <summary>The plan-map-synth planner cassette — the transcript the real-model phase-authorship test records/replays.</summary>
    public const string PlannerCassetteFileName = "plan-map-synth-planner.json";

    public static string PlannerCassettePath => Path.Combine(CassettesDir(), PlannerCassetteFileName);

    /// <summary>The source-tree Cassettes/ dir, resolved relative to this file: Infrastructure/ → Workflows/ → Workflows/Cassettes/.</summary>
    private static string CassettesDir([CallerFilePath] string thisFilePath = "")
    {
        var infrastructureDir = Path.GetDirectoryName(thisFilePath)!;
        var workflowsDir = Path.GetDirectoryName(infrastructureDir)!;
        return Path.Combine(workflowsDir, "Cassettes");
    }
}
