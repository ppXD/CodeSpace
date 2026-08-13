using System.Text.RegularExpressions;
using CodeSpace.IntegrationTests.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The teeth behind the one scheduling rule the fakes cannot enforce themselves. Every fake CLI ARMS a PROCESS-WIDE
/// env var (<c>CodexHarness.CommandEnvVar</c> / <c>ClaudeCodeHarness.CommandEnvVar</c>) in its constructor and clears
/// it on dispose, and xUnit runs different COLLECTIONS in parallel. So two fake-arming classes in DIFFERENT
/// collections re-point the var underneath each other's in-flight agents — and nothing throws: the victim's agents
/// simply spawn the other class's script and the run carries on with the wrong CLI. It surfaces as whichever
/// assertion happens to notice (a null <c>Patch</c>, a merge that should conflict reporting Clean, a Failed agent
/// when the var was cleared to an absent real binary), so a DIFFERENT test reds each run and none of them points at
/// the cause.
///
/// <para><see cref="FakeAgentCliMarker"/> closes the other half — a test that only READS the var can check WHAT it
/// resolved. A fake-vs-fake collision has no such tell: both sides legitimately want to own the var, so only
/// scheduling separates them. Pinning that here is what stops the rule from rotting again: it already did once, when
/// the engine-tier E2E tests moved into the E2E assembly and silently invalidated a second collection's "no other
/// test in this job mutates CommandEnvVar" premise.</para>
///
/// <para>Scanned from SOURCE for the same reason <see cref="FakeAgentCliMarkerConventionTests"/> is: constructing the
/// fakes to inspect them would arm the very env var this file exists to protect.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class FakeAgentCliCollectionConventionTests
{
    /// <summary>Matches a construction of any fake CLI — the family <see cref="FakeAgentCliMarkerConventionTests"/> pins by filename (<c>*FakeCli.cs</c>), plus the E2E assembly's own <c>FakeCodexCli</c>.</summary>
    private static readonly Regex ArmsAFakeCli = new(@"\bnew\s+[A-Za-z0-9_]*Fake[A-Za-z0-9_]*Cli\s*\(", RegexOptions.Compiled);

    [Theory]
    [InlineData("backend/tests/CodeSpace.E2ETests")]
    [InlineData("backend/tests/CodeSpace.IntegrationTests")]
    public void Every_fake_arming_test_class_shares_one_collection(string assemblyDir)
    {
        var armers = FakeArmingSources(assemblyDir);

        armers.Count.ShouldBeGreaterThan(5, $"the scan must actually find the fake-arming classes under {assemblyDir}, or this test passes by finding nothing");

        var offenders = armers
            .Where(f => !File.ReadAllText(f.FullName).Contains($"[Collection({nameof(PostgresCollection)}.{nameof(PostgresCollection.Name)})]", StringComparison.Ordinal))
            .Select(f => f.Name)
            .ToList();

        offenders.ShouldBeEmpty(
            $"every class that arms the process-wide CLI env var must carry [Collection({nameof(PostgresCollection)}.{nameof(PostgresCollection.Name)})] — xUnit runs other collections "
          + "(and every un-attributed class, which is its own implicit collection) in PARALLEL, so these would re-point the var mid-flight of each other's agents and the "
          + "victim would silently run the wrong CLI");
    }

    private static IReadOnlyList<FileInfo> FakeArmingSources(string assemblyDir)
    {
        var dir = new DirectoryInfo(Path.Combine(FindRepoRoot(), assemblyDir));

        return dir.GetFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => ArmsAFakeCli.IsMatch(File.ReadAllText(f.FullName)))
            .ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
