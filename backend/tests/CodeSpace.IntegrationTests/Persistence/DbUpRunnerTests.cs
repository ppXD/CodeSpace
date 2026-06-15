using CodeSpace.Core.Persistence.Db;
using CodeSpace.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DbUpRunnerTests
{
    private readonly PostgresFixture _fixture;

    public DbUpRunnerTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void Migration_is_idempotent_when_run_again()
    {
        var act = () => new DbUpRunner(_fixture.ConnectionString).Run();

        act.ShouldNotThrow();
    }

    [Theory]
    [InlineData("app_user")]
    [InlineData("team")]
    [InlineData("team_membership")]
    [InlineData("provider_instance")]
    [InlineData("credential")]
    [InlineData("repository")]
    [InlineData("repository_webhook")]
    [InlineData("conversation")]
    [InlineData("conversation_member")]
    [InlineData("message")]
    [InlineData("message_reference")]
    [InlineData("user_provider_identity")]
    [InlineData("agent_run")]
    [InlineData("agent_run_event")]
    [InlineData("agent_definition")]
    [InlineData("model_credential")]
    [InlineData("tool_call_ledger")]
    [InlineData("supervisor_decision")]
    public async Task Table_exists_after_migration(string tableName)
    {
        var exists = await TableExistsAsync(tableName).ConfigureAwait(false);

        exists.ShouldBeTrue($"Table '{tableName}' should exist after migration");
    }

    [Theory]
    [InlineData("credential", "ownership", "0030_credential_ownership.sql")]              // drives team-service vs personal credential governance
    [InlineData("message", "interaction_json", "0036_message_interaction.sql")]          // optional polymorphic interactive component (action cards)
    [InlineData("app_user", "is_bot", "0037_user_is_bot.sql")]                            // flags the per-team CodeSpace bot identity
    [InlineData("agent_run", "heartbeat_at", "0039_agent_run.sql")]                       // worker liveness ping for stuck-run recovery
    [InlineData("agent_run_event", "data_json", "0040_agent_run_event.sql")]              // optional structured payload on a normalized agent event
    [InlineData("agent_definition", "raw_frontmatter_jsonb", "0042_agent_definition.sql")] // verbatim imported frontmatter — lossless forward-compat
    [InlineData("model_credential", "encrypted_api_key", "0043_model_credential.sql")]     // the model API key, encrypted at rest (NULL for a keyless provider)
    [InlineData("agent_definition", "model_credential_id", "0044_agent_definition_model_credential.sql")] // persona's default model-credential reference
    [InlineData("agent_run", "runner_handle", "0045_agent_run_runner_handle.sql")]         // durable runner handle (pid + spool) for restart re-attach/recovery
    [InlineData("agent_run", "fence_epoch", "0046_agent_run_fence_epoch.sql")]             // monotonic fencing token — completion CAS rejects a reclaimed-then-revived worker
    [InlineData("agent_run", "lease_expires_at", "0047_agent_run_lease.sql")]              // DB-owned lease the worker renews; reconciler reclaims on lapse (ground-truth liveness)
    [InlineData("agent_run", "reattach_attempts", "0048_agent_run_reattach_attempts.sql")] // reconciler re-attach reclaim count; bounds attempts so an unattachable-but-alive run is abandoned, not reclaimed forever
    [InlineData("tool_call_ledger", "idempotency_key", "0049_tool_call_ledger.sql")]        // the server-derived at-most-once handle (one terminal row per run+key — the exactly-once invariant)
    [InlineData("tool_call_ledger", "approved_by_user_id", "0050_tool_call_ledger_approval.sql")] // durable-HITL approve sub-state — who approved the parked call
    [InlineData("tool_call_ledger", "approved_at", "0050_tool_call_ledger_approval.sql")]          // durable-HITL approve sub-state — NULL distinguishes not-yet-decided from approved-but-unexecuted
    [InlineData("supervisor_decision", "idempotency_key", "0053_supervisor_decision.sql")]         // PR-E E1: the server-derived at-most-once handle (one terminal row per run+key — the exactly-once invariant)
    [InlineData("supervisor_decision", "payload_jsonb", "0053_supervisor_decision.sql")]           // the emitted decision — a frozen-at-insert JOURNAL field (the immutability trigger protects it)
    [InlineData("supervisor_decision", "outcome_jsonb", "0053_supervisor_decision.sql")]           // the execution result — the deliberately-mutable CAS path
    [InlineData("supervisor_decision", "sequence", "0053_supervisor_decision.sql")]                // per-run BIGSERIAL replay cursor
    [InlineData("workflow_run", "definition_snapshot_jsonb", "0056_workflow_run_definition_snapshot.sql")] // dynamic-WF substrate: the inline frozen definition a snapshot run walks (NULL for authored runs)
    [InlineData("workflow_run", "definition_snapshot_hash", "0056_workflow_run_definition_snapshot.sql")]  // SHA-256 of the snapshot — same tamper-check as workflow_version.definition_hash
    public async Task Column_exists_after_migration(string tableName, string columnName, string addedBy)
    {
        var exists = await ColumnExistsAsync(tableName, columnName).ConfigureAwait(false);

        exists.ShouldBeTrue(
            $"Column '{tableName}.{columnName}' must exist after migrations apply — added by {addedBy}. " +
            $"If missing, that migration did not run. Diagnose with: psql -c '\\d {tableName}' against the test database.");
    }

    [Theory]
    [InlineData("repository", "project_id")]   // dropped by 0027 — see PR notes; the link table is now the sole project membership source
    public async Task Column_does_not_exist_after_migration(string tableName, string columnName)
    {
        var exists = await ColumnExistsAsync(tableName, columnName).ConfigureAwait(false);

        exists.ShouldBeFalse(
            $"Column '{tableName}.{columnName}' must NOT exist after migrations apply — it was dropped by 0027_drop_repository_project_id.sql. " +
            $"If present, either 0027 did not run, or a later migration re-added the column (unintentional regression). " +
            $"Diagnose with: psql -c '\\d {tableName}' against the test database.");
    }

    [Theory]
    [InlineData("ux_tool_call_ledger_run_key", "0049_tool_call_ledger.sql")]                  // the exactly-once invariant — UNIQUE (run, idempotency_key)
    [InlineData("idx_tool_call_ledger_team_created", "0049_tool_call_ledger.sql")]            // team-scoped audit read, newest first
    [InlineData("idx_tool_call_ledger_approval_token", "0049_tool_call_ledger.sql")]          // respond path locates a parked approval by its server-side token
    [InlineData("idx_tool_call_ledger_due_approvals", "0051_tool_call_ledger_due_approvals_index.sql")] // D3 reaper: partial index matching its undecided-past-deadline predicate (no full-table scan)
    [InlineData("idx_agent_run_inflight", "0052_agent_run_inflight_index.sql")]                          // D4a admission gate: partial (team_id) WHERE status IN ('Queued','Running') — serves both the team-scoped + global in-flight counts on the hot creation path
    [InlineData("ux_supervisor_decision_run_key", "0053_supervisor_decision.sql")]                       // PR-E E1: the exactly-once invariant — UNIQUE (supervisor_run_id, idempotency_key)
    [InlineData("idx_supervisor_decision_run_sequence", "0053_supervisor_decision.sql")]                 // the replay tape — (supervisor_run_id, sequence)
    [InlineData("idx_supervisor_decision_pending_created", "0053_supervisor_decision.sql")]              // the reaper's partial index — created_date WHERE status='Pending'
    public async Task Index_exists_after_migration(string indexName, string addedBy)
    {
        var exists = await IndexExistsAsync(indexName).ConfigureAwait(false);

        exists.ShouldBeTrue(
            $"Index '{indexName}' must exist after migrations apply — added by {addedBy}. " +
            $"If missing, that migration did not run (or the index was renamed). Diagnose with: psql -c '\\di {indexName}' against the test database.");
    }

    [Theory]
    [InlineData("workflow_run", "workflow_id", "0056_workflow_run_definition_snapshot.sql")]      // relaxed to NULL so a snapshot run carries no parent workflow
    [InlineData("workflow_run", "workflow_version", "0056_workflow_run_definition_snapshot.sql")] // relaxed to NULL so a snapshot run carries no pinned version
    public async Task Column_is_nullable_after_migration(string tableName, string columnName, string changedBy)
    {
        var isNullable = await ColumnIsNullableAsync(tableName, columnName).ConfigureAwait(false);

        isNullable.ShouldBeTrue(
            $"Column '{tableName}.{columnName}' must be NULL-able after migrations apply — relaxed by {changedBy} so a snapshot run " +
            $"(inline frozen definition, no parent workflow) can leave it NULL. If still NOT NULL, that migration did not run. " +
            $"Diagnose with: psql -c '\\d {tableName}' against the test database.");
    }

    private async Task<bool> ColumnIsNullableAsync(string tableName, string columnName)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t AND column_name = @c",
            conn);
        cmd.Parameters.AddWithValue("@t", tableName);
        cmd.Parameters.AddWithValue("@c", columnName);

        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return result is string s && s == "YES";
    }

    private async Task<bool> TableExistsAsync(string tableName)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @t)",
            conn);
        cmd.Parameters.AddWithValue("@t", tableName);

        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return (bool)result!;
    }

    private async Task<bool> ColumnExistsAsync(string tableName, string columnName)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t AND column_name = @c)",
            conn);
        cmd.Parameters.AddWithValue("@t", tableName);
        cmd.Parameters.AddWithValue("@c", columnName);

        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return (bool)result!;
    }

    private async Task<bool> IndexExistsAsync(string indexName)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM pg_indexes WHERE schemaname = 'public' AND indexname = @i)",
            conn);
        cmd.Parameters.AddWithValue("@i", indexName);

        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return (bool)result!;
    }
}
