using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Mcp;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the SQL shape of the operator audit list. A governed tool result is the durable exactly-once receipt and can
/// be arbitrarily large; the list UI polls while a run is active but never displays that body. Loading full ledger
/// entities therefore turns every poll into an unbounded body read even though serialization drops the bytes later.
/// </summary>
[Trait("Category", "Unit")]
public class ToolCallAuditReaderQueryTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=codespace;Username=none;Password=none;Timeout=1;Command Timeout=1";

    [Fact]
    public void The_audit_query_is_team_and_run_scoped_and_orders_in_the_database()
    {
        var sql = AuditQuerySql();

        sql.ShouldContain("agent_run_id");
        sql.ShouldContain("team_id");
        sql.ShouldContain("ORDER BY");
        sql.ShouldContain("created_date");
    }

    [Fact]
    public void The_audit_query_never_selects_result_or_execution_authority_columns()
    {
        var sql = AuditQuerySql();

        sql.ShouldNotContain("result_jsonb", customMessage: $"the list UI does not display the body; selecting it makes every active-run poll unbounded. SQL was:\n{sql}");
        sql.ShouldNotContain("decision_envelope_jsonb", customMessage: "decision bodies are not audit-list metadata");
        sql.ShouldNotContain("approval_token", customMessage: "the approval bearer secret must never cross the audit read seam");
        sql.ShouldNotContain("idempotency_key", customMessage: "the server-side execution authority is not operator-facing metadata");
        sql.ShouldNotContain("input_hash", customMessage: "the execution dedup hash is not operator-facing metadata");
    }

    [Fact]
    public void The_projection_keeps_every_operator_audit_field()
    {
        var sql = AuditQuerySql();

        foreach (var column in new[] { "tool_kind", "status", "created_date", "last_modified_date", "error", "approved_by_user_id", "approved_at" })
            sql.ShouldContain(column, customMessage: $"the body-free projection must retain audit column {column}. SQL was:\n{sql}");
    }

    private static string AuditQuerySql()
    {
        using var db = new CodeSpaceDbContext(new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options);

        return ToolCallAuditReader.AuditRowsQuery(db, Guid.NewGuid(), Guid.NewGuid()).ToQueryString();
    }
}
