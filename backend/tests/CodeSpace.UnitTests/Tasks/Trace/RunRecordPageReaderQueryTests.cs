using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.Messages.Tasks.Trace;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Trace;

/// <summary>
/// Pins the bounded raw-ledger query shapes without opening a database connection. The legacy Trace snapshot loads
/// every row and payload on every poll; this additive reader must stay on the indexed (run_id, sequence) keyset and
/// must never regress to COUNT/OFFSET or an entity-shaped SELECT.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RunRecordPageReaderQueryTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=codespace;Username=none;Password=none;Timeout=1;Command Timeout=1";

    [Fact]
    public void Run_admission_is_exact_team_scoped_metadata_only()
    {
        using var db = BuildContext();
        var sql = RunRecordPageReader.RunStatusQuery(db, Guid.NewGuid(), Guid.NewGuid()).ToQueryString();

        sql.ShouldContain("team_id");
        sql.ShouldContain("id");
        sql.ShouldContain("status");
        sql.ShouldNotContain("outputs_jsonb", customMessage: $"the page precheck needs only status; run bodies must remain in PostgreSQL. SQL was:\n{sql}");
    }

    [Fact]
    public void Tail_and_older_pages_use_descending_keysets_without_count_or_offset()
    {
        using var db = BuildContext();
        var tail = RunRecordPageReader.TailRowsQuery(db, Guid.NewGuid(), 101).ToQueryString();
        var older = RunRecordPageReader.OlderRowsQuery(db, Guid.NewGuid(), 500, 101).ToQueryString();

        AssertBounded(tail);
        AssertBounded(older);
        tail.ShouldContain("ORDER BY");
        tail.ShouldContain("DESC");
        older.ShouldContain("sequence");
        older.ShouldContain("<");
        older.ShouldContain("DESC");
    }

    [Fact]
    public void Newer_pages_use_the_forward_keyset_without_count_or_offset()
    {
        using var db = BuildContext();
        var sql = RunRecordPageReader.NewerRowsQuery(db, Guid.NewGuid(), 500, 101).ToQueryString();

        AssertBounded(sql);
        sql.ShouldContain("sequence");
        sql.ShouldContain(">");
        sql.ShouldContain("ORDER BY");
        sql.ShouldNotContain("DESC", customMessage: $"newer deltas must be emitted oldest-first so a cursor can advance gaplessly. SQL was:\n{sql}");
    }

    [Fact]
    public void Request_validation_is_closed_and_bounded()
    {
        new RunRecordPageRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, RunRecordPageLimits.DefaultLimit).Validate();
        new RunRecordPageRequest(Guid.NewGuid(), Guid.NewGuid(), null, long.MaxValue, RunRecordPageLimits.MaxLimit).Validate();

        Should.Throw<ArgumentException>(() => new RunRecordPageRequest(Guid.NewGuid(), Guid.NewGuid(), 1, 1, 10).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new RunRecordPageRequest(Guid.NewGuid(), Guid.NewGuid(), 0, null, 10).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new RunRecordPageRequest(Guid.NewGuid(), Guid.NewGuid(), null, -1, 10).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new RunRecordPageRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, 0).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new RunRecordPageRequest(Guid.NewGuid(), Guid.NewGuid(), null, null, RunRecordPageLimits.MaxLimit + 1).Validate());
    }

    private static void AssertBounded(string sql)
    {
        sql.ShouldContain("run_id");
        sql.ShouldContain("LIMIT");
        sql.ShouldNotContain("OFFSET");
        sql.ShouldNotContain("COUNT(");
        sql.ShouldContain("id", customMessage: "metadata rows need the immutable record identity for a later exact payload read");
        sql.ShouldNotContain("payload_json", customMessage: $"record-count bounding is not byte bounding; the page hot path must never detoast a payload. SQL was:\n{sql}");
    }

    private static CodeSpaceDbContext BuildContext() => new(new DbContextOptionsBuilder<CodeSpaceDbContext>()
        .UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options);
}
