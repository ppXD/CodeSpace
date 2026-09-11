using System.Text.RegularExpressions;
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
    /// Two DIFFERENT files at the same number, both redefining the same function — what two branches cut from the
    /// same main produce when each teaches a guard a new rule.
    ///
    /// <para>DbUp journals by name, so both are unapplied and both run, in the alphabetical order of their file
    /// names, which nobody chose. For the same <c>CREATE OR REPLACE FUNCTION</c> that means the later name's body
    /// simply wins and the earlier one's rules are discarded on every database built from scratch, while a database
    /// migrated incrementally keeps whichever happened to run last — so the two disagree forever after, with no
    /// error anywhere. Neither branch can see it in review or in CI, because neither branch contains both files.</para>
    ///
    /// <para>A shared NUMBER alone is not the defect and is deliberately not asserted against: this repository has
    /// carried benign pairs since 0085, they are journalled under names every deployed database already records, and
    /// renumbering one now would make it unapplied and run it a second time. Sharing a number AND a definition is
    /// the part that loses work.</para>
    /// </summary>
    [Fact]
    public void No_two_migrations_at_one_number_redefine_the_same_function()
    {
        var collisions = MigrationFiles()
            .SelectMany(file => FunctionsDefinedIn(file).Select(function => (Number: NumberOf(Path.GetFileName(file)), Function: function, File: Path.GetFileName(file))))
            .GroupBy(entry => (entry.Number, entry.Function))
            .Where(group => group.Select(entry => entry.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => $"{group.Key.Function}() is defined by both {string.Join(" and ", group.Select(entry => entry.File).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))}")
            .ToList();

        collisions.ShouldBeEmpty(
            customMessage: "Two migrations at the same number redefine one function. DbUp runs both, alphabetically " +
                           "by file name, so the later name's body wins and the other's rules are silently discarded " +
                           "on every fresh database. Renumber the newer file to the next free number and merge the " +
                           "two bodies into it:\n" + string.Join("\n", collisions));
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

    /// <summary>The ordering prefix DbUp sorts on — everything before the first underscore.</summary>
    private static string NumberOf(string fileName) => fileName.Split('_')[0];

    private static IEnumerable<string> MigrationFiles() => Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles"), "*.sql");

    /// <summary>
    /// Line comments are stripped first: these headers cite the migrations they supersede by the statement they
    /// redefine, and a citation is not a definition. Counting one would make this fire on a file that changes nothing.
    /// </summary>
    private static IEnumerable<string> FunctionsDefinedIn(string file) =>
        Regex.Matches(Regex.Replace(File.ReadAllText(file), "--.*$", "", RegexOptions.Multiline), @"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+([A-Za-z0-9_.]+)", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.ToLowerInvariant())
            .Distinct();
}
