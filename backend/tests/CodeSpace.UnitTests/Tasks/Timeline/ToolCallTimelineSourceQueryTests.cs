using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

/// <summary>
/// Pins the SQL boundary behind the Workflow Run timeline. Tool-call results are durable exactly-once receipts and
/// can be arbitrarily large; the timeline needs only bounded metadata plus one short human detail and must not
/// materialize every receipt body while projecting a room or journal.
/// </summary>
[Trait("Category", "Unit")]
public class ToolCallTimelineSourceQueryTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=codespace;Username=none;Password=none;Timeout=1;Command Timeout=1";

    [Fact]
    public void Timeline_query_is_team_scoped_batched_and_extracts_only_a_bounded_detail()
    {
        var sql = TimelineQuerySql();

        sql.ShouldContain("team_id");
        sql.ShouldContain("agent_run_id");
        sql.ShouldContain("workflow_run_id");
        sql.ShouldContain("INNER JOIN agent_run", customMessage: "one database query scopes calls through their exact Workflow Run and carries node identity without an id-list round trip");
        sql.ShouldContain("ORDER BY");
        sql.ShouldContain("result_jsonb ->>", customMessage: "PostgreSQL extracts the few human detail fields; the CLR never receives the full receipt");
        sql.ShouldContain("left(", Case.Insensitive, customMessage: "even a maliciously large summary field stays bounded on the wire");
        sql.ShouldContain("left(t.error", Case.Insensitive, customMessage: "a large failure receipt remains durable but cannot bloat the observation projection");
        sql.ShouldNotContain("decision_envelope_jsonb");
        sql.ShouldNotContain("approval_token");
        sql.ShouldNotContain("idempotency_key");
        sql.ShouldNotContain("input_hash");
        sql.ShouldNotContain("\"ResultJson\"");
    }

    private static string TimelineQuerySql()
    {
        using var db = new CodeSpaceDbContext(new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options);

        return ToolCallTimelineSource.ToolCallRowsQuery(db, Guid.NewGuid(), Guid.NewGuid()).ToQueryString();
    }
}
