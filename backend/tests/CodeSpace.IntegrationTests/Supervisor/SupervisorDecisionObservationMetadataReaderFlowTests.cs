using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor.Observation;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Supervisor;

/// <summary>
/// Real-Postgres pins for the internal bounded supervisor observation foundation. No test reaches it through Room,
/// Journal or timeline because this slice intentionally does not cut #1615's production consumers over.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorDecisionObservationMetadataReaderFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorDecisionObservationMetadataReaderFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Missing_and_foreign_runs_are_conflated_while_an_owned_empty_run_is_a_real_empty_page()
    {
        var ownerTeam = await SeedTeamAsync();
        var foreignTeam = await SeedTeamAsync();
        var ownedRun = await SeedRunAsync(ownerTeam);
        var foreignRun = await SeedRunAsync(foreignTeam);
        await InsertAsync(foreignTeam, foreignRun, "secret-kind");

        (await StoryAsync(ownerTeam, foreignRun)).ShouldBeNull();
        (await StoryAsync(ownerTeam, Guid.NewGuid())).ShouldBeNull();
        (await ChangesAsync(ownerTeam, foreignRun)).ShouldBeNull();

        var empty = await StoryAsync(ownerTeam, ownedRun);
        empty.ShouldNotBeNull();
        empty.Items.ShouldBeEmpty();
        empty.SnapshotRevision.ShouldBe(0);
        empty.HeadRevision.ShouldBe(0);
        empty.HasMore.ShouldBeFalse();
        var cursor = DecodeStory(empty.NextNewerCursor, ownerTeam, ownedRun);
        cursor.StoryOrder.ShouldBe(0);
        cursor.SnapshotRevision.ShouldBe(0);
    }

    [Fact]
    public async Task Tail_older_and_newer_are_bounded_ascending_scope_bound_and_preserve_open_kind_and_closed_status_truth()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId);
        var inserted = new List<DecisionRow>();
        for (var i = 1; i <= 7; i++) inserted.Add(await InsertAsync(teamId, runId, i == 2 ? "future.quantum-kind" : $"kind-{i}"));
        await SetStatusAsync(inserted[1].DecisionId, "FutureTerminal");
        await SetStatusAsync(inserted[2].DecisionId, "");

        var tail = (await StoryAsync(teamId, runId, SupervisorDecisionObservationStoryPageMode.Tail, null, 3))!;
        tail.Mode.ShouldBe(nameof(SupervisorDecisionObservationStoryPageMode.Tail));
        tail.RequestCursor.ShouldBeNull();
        tail.Limit.ShouldBe(3);
        tail.Items.Select(item => item.DecisionId).ShouldBe(inserted.Skip(4).Select(item => item.DecisionId));
        tail.Items.Select(item => item.StoryOrder).ShouldBeInOrder();
        tail.HasMore.ShouldBeTrue();
        tail.NextOlderCursor.ShouldNotBeNull();
        var initialSnapshot = tail.SnapshotRevision;

        var seen = tail.Items.Select(item => item.DecisionId).ToHashSet();
        var olderCursor = tail.NextOlderCursor;
        while (olderCursor != null)
        {
            var older = (await StoryAsync(teamId, runId, SupervisorDecisionObservationStoryPageMode.Older, olderCursor, 3))!;
            older.Mode.ShouldBe(nameof(SupervisorDecisionObservationStoryPageMode.Older));
            older.RequestCursor.ShouldBe(olderCursor);
            older.SnapshotRevision.ShouldBe(initialSnapshot);
            older.Items.Select(item => item.StoryOrder).ShouldBeInOrder();
            foreach (var item in older.Items) seen.Add(item.DecisionId).ShouldBeTrue("story keyset pages must not overlap");
            olderCursor = older.NextOlderCursor;
        }
        seen.Count.ShouldBe(7);

        var all = (await StoryAsync(teamId, runId, SupervisorDecisionObservationStoryPageMode.Newer,
            new SupervisorDecisionObservationStoryCursor(teamId, runId, 0, initialSnapshot).Encode(), 20))!;
        all.Items.Single(item => item.DecisionId == inserted[1].DecisionId).DecisionKind.ShouldBe("future.quantum-kind", "decision kind is an open raw discriminator");
        all.Items.Single(item => item.DecisionId == inserted[1].DecisionId).Status.ShouldBe(SupervisorDecisionObservationStatus.Corrupt);
        all.Items.Single(item => item.DecisionId == inserted[2].DecisionId).Status.ShouldBe(SupervisorDecisionObservationStatus.LegacyUnknown);

        await using var pendingInsertConnection = new NpgsqlConnection(_fixture.ConnectionString);
        await pendingInsertConnection.OpenAsync();
        await using var pendingInsert = await pendingInsertConnection.BeginTransactionAsync();
        var appended = await InsertInTransactionAsync(pendingInsertConnection, pendingInsert, teamId, runId, "after-tail");
        var beforeInsertCommit = (await StoryAsync(teamId, runId, SupervisorDecisionObservationStoryPageMode.Newer, tail.NextNewerCursor, 3))!;
        beforeInsertCommit.Items.ShouldBeEmpty("an uncommitted story allocation is not observable");
        await pendingInsert.CommitAsync();

        var newer = (await StoryAsync(teamId, runId, SupervisorDecisionObservationStoryPageMode.Newer, tail.NextNewerCursor, 3))!;
        newer.Items.Select(item => item.DecisionId).ShouldBe([appended.DecisionId]);
        newer.SnapshotRevision.ShouldBe(initialSnapshot);
        DecodeStory(newer.NextNewerCursor, teamId, runId).StoryOrder.ShouldBe(appended.StoryOrder);
    }

    [Fact]
    public async Task Change_feed_captures_terminal_and_outcome_enrichment_without_body_bytes_and_rollback_publishes_nothing()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId);
        var decision = await InsertLargeAsync(teamId, runId);
        var story = (await StoryAsync(teamId, runId))!;
        var afterSnapshot = new SupervisorDecisionObservationChangeCursor(teamId, runId, story.SnapshotRevision).Encode();

        await using var pendingUpdateConnection = new NpgsqlConnection(_fixture.ConnectionString);
        await pendingUpdateConnection.OpenAsync();
        await using var pendingUpdate = await pendingUpdateConnection.BeginTransactionAsync();
        await ExecuteLargeOutcomeUpdateAsync(pendingUpdateConnection, pendingUpdate, decision.DecisionId);
        var beforeUpdateCommit = (await ChangesAsync(teamId, runId, afterSnapshot, 10))!;
        beforeUpdateCommit.Items.ShouldBeEmpty("a revision is not observable before its transaction commits");
        await pendingUpdate.CommitAsync();

        var changes = (await ChangesAsync(teamId, runId, afterSnapshot, 10))!;

        changes.RequestCursor.ShouldBe(afterSnapshot);
        changes.SnapshotRevision.ShouldBe(story.SnapshotRevision);
        changes.HeadRevision.ShouldBeGreaterThan(changes.SnapshotRevision);
        changes.HasMore.ShouldBeFalse();
        var changed = changes.Items.ShouldHaveSingleItem();
        changed.DecisionId.ShouldBe(decision.DecisionId);
        changed.Status.ShouldBe(SupervisorDecisionObservationStatus.Succeeded);
        changed.ObservationRevision.ShouldBe(changes.HeadRevision);
        changed.ErrorState.ShouldBe(SupervisorDecisionObservationErrorState.Truncated);
        changed.ErrorPrefix!.Length.ShouldBe(SupervisorDecisionObservationMetadataReader.ErrorPrefixMaximumChars);
        changed.ErrorTotalBytes.ShouldBe(3_000);

        var wire = JsonSerializer.Serialize(changes);
        wire.ShouldNotContain("PAYLOAD-SENTINEL");
        wire.ShouldNotContain("OUTCOME-SENTINEL");
        wire.Length.ShouldBeLessThan(8_000, "2 MiB source bodies must contribute zero bytes to the observation DTO");

        var committedHead = changes.HeadRevision;
        await UpdateLargeOutcomeAsync(decision.DecisionId, commit: false);
        var afterCommit = changes.NextCursor;
        var noRolledBackChange = (await ChangesAsync(teamId, runId, afterCommit, 10))!;
        noRolledBackChange.Items.ShouldBeEmpty();
        noRolledBackChange.HeadRevision.ShouldBe(committedHead, "a consumed sequence value from a rollback is an honest gap, not a visible change");
        noRolledBackChange.NextCursor.ShouldBe(afterCommit);
    }

    [Fact]
    public async Task Ten_thousand_rows_use_both_0161_keyset_indexes_without_seqscan_sort_count_or_offset()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId);
        await InsertFloodAsync(teamId, runId, 10_050);

        var tail = (await StoryAsync(teamId, runId, SupervisorDecisionObservationStoryPageMode.Tail, null, 500))!;
        tail.Items.Count.ShouldBe(500);
        tail.Items.Select(item => item.StoryOrder).ShouldBeInOrder();
        tail.HasMore.ShouldBeTrue();

        var changes = (await ChangesAsync(teamId, runId, null, 500))!;
        changes.Items.Count.ShouldBe(500);
        changes.Items.Select(item => item.ObservationRevision).ShouldBeInOrder();
        changes.HasMore.ShouldBeTrue();

        var storyPlan = await ExplainAsync(SupervisorDecisionObservationMetadataReader.OlderSql, teamId, runId, long.MaxValue, 501);
        storyPlan.ShouldContain("ux_supervisor_decision_run_story_order");
        storyPlan.ShouldNotContain("Seq Scan on supervisor_decision");
        storyPlan.ShouldNotContain("Sort");

        var changePlan = await ExplainAsync(SupervisorDecisionObservationMetadataReader.ChangesSql, teamId, runId, 0, 501);
        changePlan.ShouldContain("ux_supervisor_decision_run_observation_revision");
        changePlan.ShouldNotContain("Seq Scan on supervisor_decision");
        changePlan.ShouldNotContain("Sort");
    }

    private async Task<SupervisorDecisionObservationStoryPage?> StoryAsync(Guid teamId, Guid runId, SupervisorDecisionObservationStoryPageMode mode = SupervisorDecisionObservationStoryPageMode.Tail, string? cursor = null, int limit = SupervisorDecisionObservationPageLimits.DefaultLimit)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISupervisorDecisionObservationMetadataReader>()
            .ReadStoryPageAsync(new SupervisorDecisionObservationStoryPageRequest(teamId, runId, mode, cursor, limit), CancellationToken.None);
    }

    private async Task<SupervisorDecisionObservationChangePage?> ChangesAsync(Guid teamId, Guid runId, string? cursor = null, int limit = SupervisorDecisionObservationPageLimits.DefaultLimit)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISupervisorDecisionObservationMetadataReader>()
            .ReadChangesAsync(new SupervisorDecisionObservationChangePageRequest(teamId, runId, cursor, limit), CancellationToken.None);
    }

    private static SupervisorDecisionObservationStoryCursor DecodeStory(string value, Guid teamId, Guid runId)
    {
        SupervisorDecisionObservationStoryCursor.TryDecode(value, teamId, runId, out var cursor).ShouldBeTrue();
        return cursor;
    }

    private async Task<DecisionRow> InsertAsync(Guid teamId, Guid runId, string kind)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO supervisor_decision
                (id, team_id, supervisor_run_id, decision_kind, idempotency_key, input_hash, status,
                 payload_jsonb, fence_epoch, created_by, last_modified_by, story_order, observation_revision)
            VALUES
                (@id, @team, @run, @kind, @key, repeat('0', 64), 'Pending', '{}', 0, @actor, @actor, -1, -1)
            RETURNING story_order, observation_revision
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("team", teamId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("key", $"{kind}:{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("actor", SystemUsers.SeederId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new DecisionRow(id, reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<DecisionRow> InsertInTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid teamId, Guid runId, string kind)
    {
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO supervisor_decision
                (id, team_id, supervisor_run_id, decision_kind, idempotency_key, input_hash, status,
                 payload_jsonb, fence_epoch, created_by, last_modified_by, story_order, observation_revision)
            VALUES
                (@id, @team, @run, @kind, @key, repeat('0', 64), 'Pending', '{}', 0, @actor, @actor, -1, -1)
            RETURNING story_order, observation_revision
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("team", teamId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("key", $"{kind}:{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("actor", SystemUsers.SeederId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new DecisionRow(id, reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<DecisionRow> InsertLargeAsync(Guid teamId, Guid runId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var decisionId = Guid.NewGuid();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO supervisor_decision
                (id, team_id, supervisor_run_id, decision_kind, idempotency_key, input_hash, status,
                 payload_jsonb, fence_epoch, created_by, last_modified_by, story_order, observation_revision)
            VALUES
                (@id, @team, @run, 'large-baggage-source', @key, repeat('0', 64), 'Pending',
                 jsonb_build_object('body', repeat('PAYLOAD-SENTINEL', 140000)), 0, @actor, @actor, -1, -1)
            RETURNING story_order, observation_revision
            """, connection);
        insert.Parameters.AddWithValue("id", decisionId);
        insert.Parameters.AddWithValue("team", teamId);
        insert.Parameters.AddWithValue("run", runId);
        insert.Parameters.AddWithValue("key", $"large:{Guid.NewGuid():N}");
        insert.Parameters.AddWithValue("actor", SystemUsers.SeederId);
        await using var reader = await insert.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new DecisionRow(decisionId, reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task SetStatusAsync(Guid decisionId, string status)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE supervisor_decision SET status = @status WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", decisionId);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
    }

    private async Task UpdateLargeOutcomeAsync(Guid decisionId, bool commit)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteLargeOutcomeUpdateAsync(connection, transaction, decisionId);
        if (commit) await transaction.CommitAsync();
        else await transaction.RollbackAsync();
    }

    private static async Task ExecuteLargeOutcomeUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid decisionId)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE supervisor_decision
            SET status = 'Succeeded',
                outcome_jsonb = jsonb_build_object('body', repeat('OUTCOME-SENTINEL', 130000)),
                error = repeat('界', 1000),
                last_modified_date = NOW()
            WHERE id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", decisionId);
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    private async Task InsertFloodAsync(Guid teamId, Guid runId, int count)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO supervisor_decision
                (id, team_id, supervisor_run_id, decision_kind, idempotency_key, input_hash, status,
                 payload_jsonb, fence_epoch, created_by, last_modified_by, story_order, observation_revision)
            SELECT md5(@salt || value::text)::uuid, @team, @run, 'flood.kind', @salt || ':' || value::text,
                   repeat('0', 64), 'Pending', '{}', 0, @actor, @actor, -1, -1
            FROM generate_series(1, @count) AS value
            """, connection);
        insert.Parameters.AddWithValue("salt", Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("team", teamId);
        insert.Parameters.AddWithValue("run", runId);
        insert.Parameters.AddWithValue("actor", SystemUsers.SeederId);
        insert.Parameters.AddWithValue("count", count);
        await insert.ExecuteNonQueryAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE supervisor_decision", connection);
        await analyze.ExecuteNonQueryAsync();
    }

    private async Task<string> ExplainAsync(string sql, Guid teamId, Guid runId, long cursor, int take)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + sql, connection);
        command.Parameters.AddWithValue("team_id", teamId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("cursor", cursor);
        command.Parameters.AddWithValue("take", take);
        command.Parameters.AddWithValue("error_chars", SupervisorDecisionObservationMetadataReader.ErrorPrefixMaximumChars);
        var lines = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private async Task<Guid> SeedTeamAsync()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        return teamId;
    }

    private async Task<Guid> SeedRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Failure,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    private sealed record DecisionRow(Guid DecisionId, long StoryOrder, long ObservationRevision);
}
