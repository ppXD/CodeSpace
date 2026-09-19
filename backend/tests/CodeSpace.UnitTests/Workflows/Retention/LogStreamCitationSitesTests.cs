using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Retention.Cursors;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Retention;

/// <summary>
/// The enumeration of everything that can still name a log stream's CAS object. It is pinned for the same reason
/// <c>ArtifactReferenceOracle.ReferenceSites</c> is: no foreign key points at an <c>artifact_object</c> from most of
/// these places, so the list IS the completeness argument, and a citer missing from it makes the cursor answer
/// "unreferenced" about bytes something still reaches.
///
/// <para>Two checks keep the list from being decoration. One reads the EF model rather than this list, so a future
/// column that names an artifact object reds here even though nobody thought about retention while adding it. The
/// other reads the cursor's own source, so an entry ADDED to the list without a probe beside it reds too — a pinned
/// list nothing cross-checks is a comment with a test around it.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class LogStreamCitationSitesTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Every_citer_of_a_log_streams_bytes_is_pinned()
    {
        LogStreamRetentionCursor.CitationSites.ShouldBe(new[]
        {
            // The qualification pin: a sealed result still cites this stream.
            ("paired_qualification_result_pin", "pinned_id"),

            // Another stream's segment. The CAS is content-addressed, so two captures of identical output — the
            // ordinary case for short or empty streams — are ONE object under two names.
            ("agent_run_log_segment", "artifact_object_id"),

            // An artifact stored as the same routed object.
            ("workflow_artifact", "cas_artifact_object_id"),
        }, ignoreOrder: true);
    }

    [Fact]
    public void A_mapped_column_that_names_an_artifact_object_and_is_not_probed_fails_this_test()
    {
        using var db = BuildContext();
        var probed = LogStreamRetentionCursor.CitationSites.Select(site => $"{site.Table}.{site.Column}").ToHashSet(StringComparer.Ordinal);

        var mapped = db.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties().Select(property => (Table: entity.GetTableName(), Column: property.GetColumnName())))
            .Where(column => column.Table is not null && column.Column is not null && IsObjectSoftLink(column.Table!, column.Column!))
            .Select(column => $"{column.Table}.{column.Column}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        mapped.Where(column => !probed.Contains(column)).ShouldBeEmpty(
            "a column that names an artifact_object which the log-stream cursor does not probe would let it purge bytes that row still reaches — "
            + $"add it to {nameof(LogStreamRetentionCursor)}.{nameof(LogStreamRetentionCursor.CitationSites)} and to the probe beside it");
    }

    /// <summary>
    /// Every entry in the list has a probe beside it, checked against the cursor's own source: a site is only a site
    /// if something asks it. The check is textual on purpose — the probes are LINQ over EF entities, so there is no
    /// runtime handle to count, and the alternative (trusting the list) is what this test exists to refuse.
    /// </summary>
    [Fact]
    public void Every_pinned_citation_site_is_actually_probed_by_the_cursor()
    {
        using var db = BuildContext();
        var source = File.ReadAllText(Path.Combine(ProductionSourceRoot(), "CodeSpace.Core", "Services", "Workflows", "Retention", "Cursors", $"{nameof(LogStreamRetentionCursor)}.cs"));

        var unprobed = LogStreamRetentionCursor.CitationSites
            .Select(site => (site.Table, site.Column, Member: MemberOf(db, site.Table, site.Column)))
            .Where(site => !source.Contains($".{site.Member}", StringComparison.Ordinal))
            .Select(site => $"{site.Table}.{site.Column} (no use of .{site.Member})")
            .ToList();

        unprobed.ShouldBeEmpty(
            $"a site listed in {nameof(LogStreamRetentionCursor.CitationSites)} that nothing reads is a claim the cursor does not keep — "
            + "add the probe, or take the entry out:\n  " + string.Join("\n  ", unprobed));
    }

    /// <summary>The CLR property a column is mapped from — the name the cursor's LINQ has to mention if it reads that column at all.</summary>
    private static string MemberOf(CodeSpaceDbContext db, string table, string column)
    {
        var entity = db.Model.GetEntityTypes().FirstOrDefault(type => type.GetTableName() == table)
            ?? throw new InvalidOperationException($"'{table}' is not mapped, so a citation site names a table this build does not have.");

        return (entity.GetProperties().FirstOrDefault(property => property.GetColumnName() == column)
            ?? throw new InvalidOperationException($"'{table}.{column}' is not mapped, so a citation site names a column this build does not have.")).Name;
    }

    private static string ProductionSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException($"'backend/src' was not found above '{AppContext.BaseDirectory}', so the probes were never checked. Run the unit suite from the repository checkout.");
    }

    /// <summary>
    /// A column names a CAS object when it ends in <c>artifact_object_id</c> — which is a NAMING convention, not a
    /// foreign key, so it catches a new column added in that shape and nothing else. A citer that named an object
    /// under some other column name would pass this check; the pinned list above is what a reviewer reads for the
    /// complete answer. Three tables are excluded, each for a stated reason rather than because it was inconvenient:
    ///
    /// <para><c>artifact_object</c> and <c>artifact_location</c> are the object's own identity and its placements,
    /// not a second holder of its bytes — the cursor reads both directly, and a purge is precisely what advances a
    /// location's state. <c>artifact_transfer_intent</c> is excluded because a saga in flight CANNOT name an object:
    /// <c>ck_artifact_transfer_intent_outcome</c> requires the column to be null on every non-terminal row, so every
    /// row that names one is a finished transfer, and a transfer is what wrote each log segment in the first place.
    /// An integration test asserts that refusal rather than trusting this comment.</para>
    /// </summary>
    private static bool IsObjectSoftLink(string table, string column) =>
        column.EndsWith("artifact_object_id", StringComparison.Ordinal)
        && table is not ("artifact_object" or "artifact_location" or "artifact_cas_purge_claim" or "artifact_transfer_intent");

    private static CodeSpaceDbContext BuildContext() =>
        new(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options);
}
