using System.Text.RegularExpressions;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Infrastructure.Jobs;

/// <summary>
/// The teeth behind <see cref="PerClassJobClientReset"/>. It runs only for a collection whose DEFINITION declares it, and
/// each test assembly declares its own definition over the same fixture, so a new or edited definition can drop it with
/// nothing failing — until a class that leaves the shared job client record-only happens to run ahead of one that drains
/// its own jobs, which is the order-dependent failure the reset exists to rule out. Scanned from SOURCE because the E2E
/// definition lives in an assembly this one cannot reference.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PostgresCollectionJobClientResetConventionTests
{
    private static readonly Regex CollectionOverTheFixture = new(@"\bclass\s+(?<name>\w+)\s*:(?<bases>[^{;]*\bICollectionFixture<\s*PostgresFixture\s*>[^{;]*)", RegexOptions.Compiled);

    private static readonly Regex DeclaresTheReset = new($@"\bIClassFixture<\s*(\w+\.)*{nameof(PerClassJobClientReset)}\s*>", RegexOptions.Compiled);

    [SkippableTheory]
    [InlineData("backend/tests/CodeSpace.E2ETests")]
    [InlineData("backend/tests/CodeSpace.IntegrationTests")]
    public void Every_collection_over_the_postgres_fixture_hands_each_class_a_fresh_job_client(string assemblyDir)
    {
        Skip.If(!FakeCliSourceLocator.RepoRootFound(), "the backend source tree is not alongside the test binaries (e.g. a published/copied test run) — this convention can only police what it can read");

        var definitions = CollectionDefinitions(assemblyDir);

        definitions.ShouldNotBeEmpty($"the scan must find the {nameof(PostgresFixture)} collection definitions under {assemblyDir}, or this test passes by finding nothing");

        var offenders = definitions.Where(d => !DeclaresTheReset.IsMatch(d.Bases)).Select(d => d.Name).ToList();

        offenders.ShouldBeEmpty(
            $"every collection over {nameof(PostgresFixture)} must also declare IClassFixture<{nameof(PerClassJobClientReset)}> — the fixture holds ONE job client, and without the reset "
          + "a class that leaves it record-only strands whichever class runs next, so a suite that passes alone fails in company");
    }

    private static IReadOnlyList<(string Name, string Bases)> CollectionDefinitions(string assemblyDir) =>
        new DirectoryInfo(Path.Combine(FakeCliSourceLocator.FindRepoRoot(), assemblyDir))
            .GetFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !FakeCliSourceLocator.IsBuildOutput(f))
            .SelectMany(f => CollectionOverTheFixture.Matches(File.ReadAllText(f.FullName)))
            .Select(m => (m.Groups["name"].Value, m.Groups["bases"].Value))
            .ToList();
}
