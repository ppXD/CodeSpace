using CodeSpace.Core.Persistence.Db;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// What DbUp can see before it is allowed to change anything.
///
/// <para>Two failure modes, opposite to each other, and both silent. Discovering NOTHING makes
/// <c>PerformUpgrade</c> report success having applied nothing, so the process starts against an
/// unmigrated database and the first request to touch a missing column carries the only evidence.
/// Discovering everything TWICE is worse: DbUp journals a script by NAME, and the same file reached
/// through two providers arrives under two different names, so every migration in the repository
/// would be applied a second time to a database that already has them.</para>
/// </summary>
[Trait("Category", "Unit")]
public class MigrationDiscoveryTests
{
    [Fact]
    public void Every_migration_is_discovered_exactly_once()
    {
        var names = DbUpRunner.DiscoverScriptNames();

        var duplicates = names
            .GroupBy(FileNameOf, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} discovered as [{string.Join(", ", group)}]")
            .ToList();

        duplicates.ShouldBeEmpty(
            customMessage: "The same migration is reachable through more than one script provider, under a different " +
                           "name each way. DbUp journals by name, so on any existing database the second name is " +
                           "unapplied and every one of these would run again:\n" + string.Join("\n", duplicates));
    }

    /// <summary>
    /// Zero is never legitimate — the repository has shipped migrations since 0001 — so this failing
    /// means the scripts did not travel with the build, which is the shape a packaging change takes.
    /// </summary>
    [Fact]
    public void Migrations_travel_with_the_build()
    {
        DbUpRunner.DiscoverScriptNames().Count.ShouldBeGreaterThan(100,
            customMessage: "DbUp found (almost) no migration scripts. They are copied next to the assembly by the " +
                           "Content item in CodeSpace.Core.csproj; if that stops happening, a deployed image migrates " +
                           "nothing and reports success.");
    }

    /// <summary>The name DbUp journals is the file name, which is what every existing database already records.</summary>
    [Fact]
    public void Scripts_are_journalled_under_their_bare_file_name()
    {
        DbUpRunner.DiscoverScriptNames().ShouldContain(
            name => name.EndsWith("0001_initial.sql", StringComparison.OrdinalIgnoreCase),
            customMessage: "0001_initial.sql must be discoverable. If its journalled name ever changes shape, every " +
                           "deployed database sees the whole history as unapplied.");
    }

    private static string FileNameOf(string scriptName) => scriptName.Split('.', '/', '\\')[^2] + ".sql";
}
