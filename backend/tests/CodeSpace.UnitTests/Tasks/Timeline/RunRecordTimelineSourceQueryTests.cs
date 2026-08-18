using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

/// <summary>
/// Proves the lifecycle source's ledger read is a SERVER-side filter + projection, without needing a database to prove
/// it: EF translates a query before it opens a connection, so pointing the context at a closed port still yields the
/// real SQL. This is the read the DEFAULT run view walks once per turn, on a 2s poll, per viewer — and a streamed
/// 30-minute run accumulates thousands of <c>interaction.delta</c> rows, so whether the noise is dropped in SQL or in
/// C# after every <c>payload_json</c> crossed the wire is the whole cost. The equivalence of the two paths is pinned
/// separately (<c>RunRecordTimelineMapTests</c> pure, <c>RunRecordTimelineFilterFlowTests</c> on real Postgres).
/// </summary>
[Trait("Category", "Unit")]
public class RunRecordTimelineSourceQueryTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=codespace;Username=none;Password=none;Timeout=1;Command Timeout=1";

    [Fact]
    public void The_record_type_filter_is_pushed_into_the_where_clause()
    {
        var sql = NarrativeQuerySql();
        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];

        where.ShouldContain("record_type", customMessage: $"the narrative filter must be a SERVER-side predicate — dropping the noise in C# means every delta / log / scope row and its payload crossed the wire first. SQL was:\n{sql}");
        where.ShouldContain("run_id", customMessage: "the filter stays run-scoped, so it is servable by idx_wrr_run_type(run_id, record_type)");
    }

    [Fact]
    public void Only_the_columns_the_map_reads_are_selected()
    {
        var sql = NarrativeQuerySql();

        sql.ShouldContain("payload_json", customMessage: "the KEPT rows' payload still has to load — the map reads error / wait_kind / reason / attempt / usage out of it");
        sql.ShouldContain("iteration_key", customMessage: "the map reads iteration_key to fold a fanned-out branch failure to Detail");
        sql.ShouldNotContain("correlation_id", customMessage: $"the map never reads correlation_id — it must stay in the database. SQL was:\n{sql}");
        sql.ShouldNotContain("parent_record_id", customMessage: $"the map never reads parent_record_id — it must stay in the database. SQL was:\n{sql}");
    }

    [Fact]
    public void Every_narrative_record_type_survives_into_the_translated_predicate()
    {
        // EF renders the map's derived set as an inlined IN list, so every type is readable in the SQL. A type missing
        // from the predicate is a timeline event the operator silently stops seeing, so assert the WHOLE set is there.
        // If a future EF/Npgsql version switches to a `= ANY(@p)` array parameter this goes red — correctly: the new
        // rendering has to be re-checked for carrying all of them before the assertion is relaxed.
        var sql = NarrativeQuerySql();

        RunRecordTimelineMap.NarrativeRecordTypes.Where(t => !sql.Contains(t, StringComparison.Ordinal))
            .ShouldBeEmpty($"every narrative record type must survive translation into the predicate. SQL was:\n{sql}");
    }

    private static string NarrativeQuerySql()
    {
        using var db = BuildContext();

        return RunRecordTimelineSource.NarrativeRecordsQuery(db, Guid.NewGuid()).ToQueryString();
    }

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options;

        return new CodeSpaceDbContext(options);
    }
}
