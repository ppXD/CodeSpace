using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.Messages.Queries.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Trace;

[Trait("Category", "Unit")]
public sealed class RunRecordPayloadReaderTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, RunRecordPayloadWire.MaximumRangeBytes + 1)]
    [InlineData(long.MaxValue, 1)]
    [InlineData(int.MaxValue, 1)]
    public void Invalid_ranges_are_rejected_before_payload_io(long offsetBytes, int limitBytes)
    {
        RunRecordPayloadWire.ValidRange(Query(offsetBytes, limitBytes)).ShouldBeFalse();
    }

    [Fact]
    public void Maximum_postgres_jsonb_range_is_admitted()
    {
        RunRecordPayloadWire.ValidRange(Query(int.MaxValue - RunRecordPayloadWire.MaximumRangeBytes,
            RunRecordPayloadWire.MaximumRangeBytes)).ShouldBeTrue();
    }

    [Fact]
    public void Bounded_sql_is_exact_team_run_record_scoped_and_does_not_scan_sibling_bodies()
    {
        var sql = RunRecordPayloadReader.RangeSql;

        sql.ShouldContain("run.team_id = @team_id");
        sql.ShouldContain("record.run_id = @run_id");
        sql.ShouldContain("record.id = @record_id");
        sql.ShouldContain("MATERIALIZED", customMessage: "the exact target's JSONB is converted once before length + range projection");
        sql.ShouldContain("substring(");
        sql.ShouldContain("@limit_bytes");
        sql.ShouldContain("LIMIT 1");
        sql.ShouldNotContain("ORDER BY");
    }

    private static ReadRunRecordPayloadRangeQuery Query(long offsetBytes, int limitBytes) => new()
    {
        RunId = Guid.NewGuid(), RecordId = Guid.NewGuid(), OffsetBytes = offsetBytes, LimitBytes = limitBytes,
    };
}
