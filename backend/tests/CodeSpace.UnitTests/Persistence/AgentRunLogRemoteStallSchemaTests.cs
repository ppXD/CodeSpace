using CodeSpace.Core.Persistence.Db;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// The two migrations that let a storage outage be legible instead of silent: a stream says its bytes are HELD, and a
/// gap can say the remote never answered. Both are pinned as SQL text because the EF model's mirror of a CHECK proves
/// only that the model agrees with itself.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunLogRemoteStallSchemaTests
{
    private const string StallMigration = "0230_agent_run_log_stream_remote_stall.sql";
    private const string ReasonMigration = "0231_capture_gap_remote_unavailable_reason.sql";

    [Fact]
    public void Stall_migration_adds_two_nullable_columns_that_are_present_together_or_not_at_all()
    {
        var sql = Read(StallMigration);

        sql.ShouldContain("remote_stall_since timestamptz NULL", Case.Insensitive);
        sql.ShouldContain("remote_stall_code  varchar(128) NULL", Case.Insensitive);
        sql.ShouldContain("(remote_stall_since IS NULL AND remote_stall_code IS NULL)", Case.Insensitive);
        sql.ShouldContain("remote_stall_since IS NOT NULL AND remote_stall_code IS NOT NULL AND btrim(remote_stall_code) <> ''", Case.Insensitive);
        sql.ShouldNotContain("DROP COLUMN", Case.Insensitive);
        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith(StallMigration, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Stall_migration_teaches_the_stream_guard_a_fifth_update_shape_that_cannot_touch_the_byte_head()
    {
        // This is the load-bearing half. agent_run_log_stream_guard() admits exactly four UPDATE shapes, and the
        // Open-state arm demands segment_count = OLD + 1 with a matching append-only segment row — so before this
        // migration a statement that moved only the stall columns was refused as an append that never happened, and
        // the producer had no way to say its bytes were queued. Losing this arm in a later redefinition of the guard
        // silently re-breaks that, which is why the arm's own name and its untouched-column list are pinned here.
        var sql = Read(StallMigration);

        sql.ShouldContain("CREATE OR REPLACE FUNCTION agent_run_log_stream_guard()", Case.Insensitive);
        sql.ShouldContain("is_remote_stall BOOLEAN := FALSE", Case.Insensitive);
        sql.ShouldContain("remote-stall statement cannot rewrite its claim, byte head or terminal state", Case.Insensitive);
        sql.ShouldContain("NOT is_claim AND NOT is_source_finalize AND NOT is_remote_stall AND NEW.state = 'Open'", Case.Insensitive);
        sql.ShouldContain("ELSIF NOT is_claim AND NOT is_source_finalize AND NOT is_remote_stall THEN", Case.Insensitive);
        sql.ShouldContain("OR NEW.remote_stall_since IS NOT NULL OR NEW.remote_stall_code IS NOT NULL", Case.Insensitive);
        sql.ShouldContain("revision <> OLD.revision + 1", Case.Insensitive, "a side channel exempt from the monotonic rule is a row two writers can disagree about");
    }

    [Fact]
    public void Reason_migration_widens_only_the_gap_vocabulary_and_keeps_it_closed()
    {
        var sql = Read(ReasonMigration);

        sql.ShouldContain("DROP CONSTRAINT ck_workflow_run_capture_gap_reason", Case.Insensitive);
        sql.ShouldContain("reason IN ('BoundExceeded', 'WriteRefused', 'ReattachTorn', 'FrameUnreadable', 'RemoteUnavailable')", Case.Insensitive);
        // The vocabulary stays closed (the IN list above is exhaustive) and no facet is added: a facet needs a
        // declarable expected COUNT, and nothing knows in advance how many segments an outage will cost.
        sql.ShouldNotContain("ALTER TABLE workflow_run_data_manifest", Case.Insensitive);
        sql.ShouldNotContain("'Unknown'", Case.Insensitive);
        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith(ReasonMigration, StringComparison.OrdinalIgnoreCase));
    }

    private static string Read(string migration) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", migration));
}
