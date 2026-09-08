using CodeSpace.Core.Persistence.Db;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public sealed class AgentRunSpoolCleanupRetrySchemaTests
{
    private const string Migration = "0206_agent_run_spool_cleanup_retry.sql";

    [Fact]
    public void Migration_persists_bounded_retry_evidence_and_indexes_only_terminal_cleanup_obligations()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", Migration));

        sql.ShouldContain("spool_cleanup_attempts integer NOT NULL DEFAULT 0", Case.Insensitive);
        sql.ShouldContain("CHECK (spool_cleanup_attempts >= 0)", Case.Insensitive);
        sql.ShouldContain("spool_cleanup_last_attempt_at timestamptz NULL", Case.Insensitive);
        sql.ShouldContain("spool_cleanup_next_attempt_at timestamptz NULL", Case.Insensitive);
        sql.ShouldContain("spool_cleanup_last_error_code varchar(64) NULL", Case.Insensitive);
        sql.ShouldContain("spool_cleanup_attempts, spool_cleanup_next_attempt_at ASC NULLS FIRST, completed_at, id", Case.Insensitive);
        sql.ShouldContain("WHERE runner_handle IS NOT NULL AND status NOT IN ('Queued', 'Running')", Case.Insensitive);
        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith(Migration, StringComparison.OrdinalIgnoreCase));
    }
}
