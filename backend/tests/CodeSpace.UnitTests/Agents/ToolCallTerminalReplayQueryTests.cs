using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Mcp;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the lost-terminal-CAS replay read to one exact ledger row. The prior run-wide entity read selected every
/// governed result body merely to find one ledger id, making replay cost proportional to all tool output in the run.
/// </summary>
[Trait("Category", "Unit")]
public class ToolCallTerminalReplayQueryTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=codespace;Username=none;Password=none;Timeout=1;Command Timeout=1";

    [Fact]
    public void The_replay_query_is_exactly_tenant_run_and_ledger_scoped_with_a_one_row_bound()
    {
        var sql = ReplayQuerySql();
        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];

        where.ShouldContain("WHERE t.id = @ledgerId AND t.agent_run_id = @agentRunId AND t.team_id = @teamId", customMessage: $"all three identities must be conjunctive exact predicates. SQL was:\n{sql}");
        sql.ShouldContain("-- @p='1'", customMessage: $"the translated bound parameter must stay exactly one. SQL was:\n{sql}");
        sql.ShouldContain("LIMIT @p", customMessage: $"replay is K=1, never a run-wide body scan. SQL was:\n{sql}");
    }

    [Fact]
    public void The_replay_query_selects_only_the_terminal_wire_authority()
    {
        var sql = ReplayQuerySql();
        var select = sql[..sql.IndexOf("FROM", StringComparison.Ordinal)];

        foreach (var column in new[] { "status", "result_jsonb", "error" })
            select.ShouldContain(column, customMessage: $"terminal replay requires {column}. SQL was:\n{sql}");

        foreach (var column in new[] { "tool_kind", "decision_envelope_jsonb", "approval_token", "approval_message_id", "approved_at", "idempotency_key", "input_hash", "fence_epoch" })
            sql.ShouldNotContain(column, customMessage: $"replay must not read unrelated authority column {column}. SQL was:\n{sql}");
    }

    private static string ReplayQuerySql()
    {
        using var db = new CodeSpaceDbContext(new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options);

        return ToolCallLedgerService.TerminalReplayQuery(db, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()).ToQueryString();
    }
}
