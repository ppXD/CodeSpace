using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres pins for the supervisor observation cursor substrate. The existing Sequence remains execution/replay
/// authority; StoryOrder is immutable narrative order and ObservationRevision is the mutable observation watermark.
/// Both are database-owned and admitted under one run-scoped transaction lock.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorDecisionObservationCursorFlowTests
{
    private const string InputHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly PostgresFixture _fixture;

    public SupervisorDecisionObservationCursorFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Same_run_insert_waits_for_the_prior_transaction_and_commit_preserves_visible_order()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        await using var firstConnection = await OpenAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        var first = await InsertAsync(firstConnection, firstTransaction, teamId, runId, storyOrder: -11, observationRevision: -12);

        var second = InsertInOwnTransactionAsync(teamId, runId);
        (await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(500)))).ShouldNotBe(second,
            "a same-run insert must wait until the earlier mutation commits; pre-commit allocation is not a safe cursor");

        await firstTransaction.CommitAsync();
        var committed = await second.WaitAsync(TimeSpan.FromSeconds(10));

        committed.StoryOrder.ShouldBeGreaterThan(first.StoryOrder);
        committed.ObservationRevision.ShouldBeGreaterThan(first.ObservationRevision);
    }

    [Fact]
    public async Task Same_run_insert_waits_for_rollback_and_the_only_visible_row_orders_after_the_aborted_allocation()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        await using var abandonedConnection = await OpenAsync();
        await using var abandonedTransaction = await abandonedConnection.BeginTransactionAsync();
        var abandoned = await InsertAsync(abandonedConnection, abandonedTransaction, teamId, runId);

        var committedTask = InsertInOwnTransactionAsync(teamId, runId);
        (await Task.WhenAny(committedTask, Task.Delay(TimeSpan.FromMilliseconds(500)))).ShouldNotBe(committedTask,
            "the contender must not allocate around an uncommitted same-run mutation");

        await abandonedTransaction.RollbackAsync();
        var committed = await committedTask.WaitAsync(TimeSpan.FromSeconds(10));

        committed.StoryOrder.ShouldBeGreaterThan(abandoned.StoryOrder, "the database cursor is gapful across rollback, never reused as false commit history");
        committed.ObservationRevision.ShouldBeGreaterThan(abandoned.ObservationRevision);
        (await CountAsync(abandoned.DecisionId)).ShouldBe(0);
        (await CountAsync(committed.DecisionId)).ShouldBe(1);
    }

    [Fact]
    public async Task Different_runs_do_not_share_the_admission_lock()
    {
        var teamId = await SeedTeamAsync();
        await using var firstConnection = await OpenAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        await InsertAsync(firstConnection, firstTransaction, teamId, Guid.NewGuid());

        var unrelated = InsertInOwnTransactionAsync(teamId, Guid.NewGuid());

        await unrelated.WaitAsync(TimeSpan.FromSeconds(3));
        await firstTransaction.RollbackAsync();
    }

    [Fact]
    public async Task Every_update_advances_revision_without_moving_story_order_and_same_transaction_keeps_the_final_revision()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var inserted = await InsertCommittedAsync(teamId, runId);
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var running = await UpdateAsync(connection, transaction, inserted.DecisionId, "Running", "{\"stage\":1}", null, forgedRevision: -50);
        var terminal = await UpdateAsync(connection, transaction, inserted.DecisionId, "Succeeded", "{\"stage\":2}", null, forgedRevision: -60);
        await transaction.CommitAsync();

        running.StoryOrder.ShouldBe(inserted.StoryOrder);
        terminal.StoryOrder.ShouldBe(inserted.StoryOrder);
        running.ObservationRevision.ShouldBeGreaterThan(inserted.ObservationRevision);
        terminal.ObservationRevision.ShouldBeGreaterThan(running.ObservationRevision);

        var final = await ReadAsync(inserted.DecisionId);
        final.Status.ShouldBe("Succeeded");
        final.OutcomeJson.ShouldBe("{\"stage\": 2}");
        final.StoryOrder.ShouldBe(inserted.StoryOrder);
        final.ObservationRevision.ShouldBe(terminal.ObservationRevision);
    }

    [Fact]
    public async Task Story_order_and_scope_identity_are_immutable_while_a_forged_revision_is_overwritten()
    {
        var teamId = await SeedTeamAsync();
        var foreignTeamId = await SeedTeamAsync();
        var inserted = await InsertCommittedAsync(teamId, Guid.NewGuid());
        await using var connection = await OpenAsync();

        await ExecuteRejectedAsync(connection, "UPDATE supervisor_decision SET story_order = story_order + 1 WHERE id = @id", inserted.DecisionId);
        await ExecuteRejectedAsync(connection, "UPDATE supervisor_decision SET team_id = @replacement WHERE id = @id", inserted.DecisionId, foreignTeamId);
        await ExecuteRejectedAsync(connection, "UPDATE supervisor_decision SET supervisor_run_id = @replacement WHERE id = @id", inserted.DecisionId, Guid.NewGuid());

        await using var transaction = await connection.BeginTransactionAsync();
        var updated = await UpdateAsync(connection, transaction, inserted.DecisionId, "Running", null, null, forgedRevision: 1);
        await transaction.CommitAsync();
        updated.ObservationRevision.ShouldBeGreaterThan(inserted.ObservationRevision,
            "the database replaces a caller-supplied revision with the next admitted value");
    }

    [Fact]
    public async Task Ef_insert_cannot_forge_database_owned_cursor_values_and_receives_the_assigned_values()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var decision = new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = runId,
            DecisionKind = "plan",
            IdempotencyKey = $"plan:{Guid.NewGuid():N}",
            InputHash = InputHash,
            Status = SupervisorDecisionStatus.Pending,
            PayloadJson = "{}",
            FenceEpoch = 0,
            StoryOrder = long.MaxValue,
            ObservationRevision = long.MaxValue,
        };

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.SupervisorDecisionRecord.Add(decision);
            await db.SaveChangesAsync();
        }

        decision.StoryOrder.ShouldBeGreaterThan(0);
        decision.StoryOrder.ShouldNotBe(long.MaxValue);
        decision.ObservationRevision.ShouldBeGreaterThan(0);
        decision.ObservationRevision.ShouldNotBe(long.MaxValue);
        var stored = await ReadAsync(decision.Id);
        stored.StoryOrder.ShouldBe(decision.StoryOrder);
        stored.ObservationRevision.ShouldBe(decision.ObservationRevision);
    }

    [Fact]
    public async Task Cursor_columns_are_required_without_pre_lock_defaults_and_legacy_story_comment_denies_commit_reconstruction()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT column_name, is_nullable, column_default,
                   col_description('supervisor_decision'::regclass, ordinal_position)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'supervisor_decision'
              AND column_name IN ('story_order', 'observation_revision')
            ORDER BY column_name
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(0).ShouldBe("observation_revision");
        reader.GetString(1).ShouldBe("NO");
        reader.IsDBNull(2).ShouldBeTrue("a column default would allocate before the run-scoped trigger lock");
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(0).ShouldBe("story_order");
        reader.GetString(1).ShouldBe("NO");
        reader.IsDBNull(2).ShouldBeTrue();
        reader.GetString(3).ShouldContain("legacy rows preserve existing BIGSERIAL allocation order");
        reader.GetString(3).ShouldContain("never claim reconstructed commit order");
        (await reader.ReadAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Story_and_revision_keyset_queries_use_their_run_indexes_without_scan_or_sort()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var inserted = await InsertCommittedAsync(teamId, runId);
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction))
            await settings.ExecuteNonQueryAsync();

        var storyPlan = await ExplainAsync(connection, transaction,
            "SELECT id FROM supervisor_decision WHERE supervisor_run_id = @run AND story_order > @cursor ORDER BY story_order LIMIT 128",
            runId, inserted.StoryOrder - 1);
        storyPlan.ShouldContain("ux_supervisor_decision_run_story_order");
        storyPlan.ShouldNotContain("Seq Scan on supervisor_decision");
        storyPlan.ShouldNotContain("Sort");

        var revisionPlan = await ExplainAsync(connection, transaction,
            "SELECT id FROM supervisor_decision WHERE supervisor_run_id = @run AND observation_revision > @cursor ORDER BY observation_revision LIMIT 128",
            runId, inserted.ObservationRevision - 1);
        revisionPlan.ShouldContain("ux_supervisor_decision_run_observation_revision");
        revisionPlan.ShouldNotContain("Seq Scan on supervisor_decision");
        revisionPlan.ShouldNotContain("Sort");
        await transaction.RollbackAsync();
    }

    private async Task<CursorRow> InsertInOwnTransactionAsync(Guid teamId, Guid runId)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var row = await InsertAsync(connection, transaction, teamId, runId);
        await transaction.CommitAsync();
        return row;
    }

    private async Task<CursorRow> InsertCommittedAsync(Guid teamId, Guid runId) => await InsertInOwnTransactionAsync(teamId, runId);

    private static async Task<CursorRow> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid teamId, Guid runId, long storyOrder = 0, long observationRevision = 0)
    {
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO supervisor_decision (
                id, team_id, supervisor_run_id, decision_kind, idempotency_key, input_hash, status, payload_jsonb,
                fence_epoch, created_by, last_modified_by, story_order, observation_revision)
            VALUES (
                @id, @team, @run, 'plan', @key, @hash, 'Pending', '{}',
                0, @actor, @actor, @story, @revision)
            RETURNING story_order, observation_revision
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("team", teamId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("key", $"plan:{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("hash", InputHash);
        command.Parameters.AddWithValue("actor", SystemUsers.SeederId);
        command.Parameters.AddWithValue("story", storyOrder);
        command.Parameters.AddWithValue("revision", observationRevision);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new CursorRow(id, reader.GetInt64(0), reader.GetInt64(1), "Pending", null);
    }

    private static async Task<CursorRow> UpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string status, string? outcomeJson, string? error, long forgedRevision)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE supervisor_decision
            SET status = @status,
                outcome_jsonb = CAST(@outcome AS jsonb),
                error = @error,
                last_modified_date = NOW(),
                observation_revision = @revision
            WHERE id = @id
            RETURNING story_order, observation_revision
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("outcome", (object?)outcomeJson ?? DBNull.Value);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("revision", forgedRevision);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new CursorRow(id, reader.GetInt64(0), reader.GetInt64(1), status, outcomeJson);
    }

    private async Task<CursorRow> ReadAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT story_order, observation_revision, status, outcome_jsonb::text
            FROM supervisor_decision
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new CursorRow(id, reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private async Task<int> CountAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*)::int FROM supervisor_decision WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteRejectedAsync(NpgsqlConnection connection, string sql, Guid id, Guid? replacement = null)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("replacement", replacement ?? Guid.NewGuid());
        await command.ExecuteNonQueryAsync().ShouldThrowAsync<PostgresException>();
        await transaction.RollbackAsync();
    }

    private static async Task<string> ExplainAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid runId, long cursor)
    {
        await using var command = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + sql, connection, transaction);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("cursor", cursor);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"supervisor-cursor-{teamId:N}", Name = "Supervisor Cursor Team", Kind = TeamKind.Workspace });
        await db.SaveChangesAsync();
        return teamId;
    }

    private sealed record CursorRow(Guid DecisionId, long StoryOrder, long ObservationRevision, string Status, string? OutcomeJson);
}
