using System.Data.Common;
using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Sessions.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SessionRunMetadataPageFlowTests
{
    private readonly PostgresFixture _fixture;

    public SessionRunMetadataPageFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Tail_and_older_are_bounded_and_a_concurrent_member_does_not_cross_the_frozen_head()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, userId, "Paged lineage");
        var runs = new List<RunFact>();
        for (var i = 0; i < 4; i++) runs.Add(await SeedRunAsync(new RunSeed(teamId, sessionId, i + 1, null, WorkflowRunStatus.Success)));
        var selector = SessionSelector(sessionId);
        var recorder = new ReadCommandRecorder();

        SessionRunMetadataPage tail;
        using (var scope = ReadScope(recorder))
            tail = (await scope.Resolve<ISessionRunMetadataPageReader>().ReadAsync(Request(teamId, selector, SessionRunMetadataPageDirection.Tail, cursor: null, limit: 2), CancellationToken.None))!;

        tail.Selector.ShouldBe(selector);
        tail.SessionId.ShouldBe(sessionId);
        tail.Direction.ShouldBe(SessionRunMetadataPageDirection.Tail);
        tail.RequestCursor.ShouldBeNull();
        tail.Limit.ShouldBe(2);
        tail.MembershipHeadRunNumber.ShouldBe(runs[^1].RunNumber);
        tail.Consistency.ShouldBe(SessionRunMetadataConsistency.MembershipHeadOnly);
        tail.Items.Select(item => item.RunId).ShouldBe(new[] { runs[2].RunId, runs[3].RunId });
        tail.Items.Select(item => item.RunNumber).ShouldBeInOrder(SortDirection.Ascending);
        tail.Items.ShouldAllBe(item => item.SourceType.State == SessionRunMetadataTextState.Complete);
        tail.Items.ShouldAllBe(item => item.ProjectionKind.State == SessionRunMetadataTextState.None);
        tail.Items.ShouldAllBe(item => item.RerunFromNodeId.State == SessionRunMetadataTextState.None);
        tail.Items.ShouldAllBe(item => item.Error.State == SessionRunMetadataTextState.None);
        tail.Omitted.ShouldBe(new SessionRunMetadataOmission { Older = true, Newer = false });
        tail.Continuation.OlderCursor.ShouldNotBeNull();
        tail.Continuation.ReturnToTail.ShouldBeFalse();

        var concurrent = await SeedRunAsync(new RunSeed(teamId, sessionId, 5, null, WorkflowRunStatus.Running));
        concurrent.RunNumber.ShouldBeGreaterThan(tail.MembershipHeadRunNumber);
        await SetRunStatusAsync(runs[1].RunId, WorkflowRunStatus.Failure);

        SessionRunMetadataPage older;
        using (var scope = _fixture.BeginScope())
            older = (await scope.Resolve<ISessionRunMetadataPageReader>().ReadAsync(Request(teamId, selector, SessionRunMetadataPageDirection.Older, tail.Continuation.OlderCursor, limit: 2), CancellationToken.None))!;

        older.Selector.ShouldBe(selector);
        older.Direction.ShouldBe(SessionRunMetadataPageDirection.Older);
        older.RequestCursor.ShouldBe(tail.Continuation.OlderCursor);
        older.Limit.ShouldBe(2);
        older.MembershipHeadRunNumber.ShouldBe(tail.MembershipHeadRunNumber);
        older.Items.Select(item => item.RunId).ShouldBe(new[] { runs[0].RunId, runs[1].RunId });
        older.Items.Single(item => item.RunId == runs[1].RunId).Status.ShouldBe(WorkflowRunStatus.Failure,
            "membership is frozen across pages, while status is deliberately observed fresh");
        older.Items.ShouldNotContain(item => item.RunId == concurrent.RunId);
        older.Omitted.ShouldBe(new SessionRunMetadataOmission { Older = false, Newer = true });
        older.Continuation.OlderCursor.ShouldBeNull();
        older.Continuation.ReturnToTail.ShouldBeTrue();

        var command = recorder.Commands.ShouldHaveSingleItem("Tail admission, membership head, and limit+1 rows share one statement snapshot");
        command.ShouldContain("session-run-metadata-page", Case.Insensitive);
        command.ShouldContain("LIMIT", Case.Insensitive);
        command.ShouldNotContain("OFFSET", Case.Insensitive);
        command.ShouldNotContain("COUNT(", Case.Insensitive);
        command.ShouldNotContain("outputs_jsonb", Case.Insensitive);
        command.ShouldNotContain("normalized_payload_json", Case.Insensitive);
        command.ShouldNotContain("goal", Case.Insensitive);
        command.ShouldNotContain("result", Case.Insensitive);
        command.ShouldNotContain("artifact", Case.Insensitive);
        command.ShouldNotContain("manifest", Case.Insensitive);
    }

    [Fact]
    public async Task Run_anchor_is_team_exact_and_echoes_the_requested_lineage_coordinate()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, foreignUserId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, userId, "Anchored lineage");
        var root = await SeedRunAsync(new RunSeed(teamId, sessionId, 1, null, WorkflowRunStatus.Failure));
        var attempt = await SeedRunAsync(new RunSeed(teamId, sessionId, null, root.RunId, WorkflowRunStatus.Success));
        var selector = AnchorSelector(attempt.RunId);

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<ISessionRunMetadataPageReader>();
        var page = await reader.ReadAsync(Request(teamId, selector), CancellationToken.None);

        page.ShouldNotBeNull();
        page!.Selector.ShouldBe(selector);
        page.SessionId.ShouldBe(sessionId);
        page.AnchorRootRunId.ShouldBe(root.RunId);
        page.Items.Select(item => item.RunId).ShouldBe(new[] { root.RunId, attempt.RunId });
        (await reader.ReadAsync(Request(foreignTeamId, selector), CancellationToken.None)).ShouldBeNull();
        (await reader.ReadAsync(Request(teamId, AnchorSelector(Guid.NewGuid())), CancellationToken.None)).ShouldBeNull();

        var foreignSession = await SeedSessionAsync(foreignTeamId, foreignUserId, "Foreign");
        (await reader.ReadAsync(Request(teamId, SessionSelector(foreignSession)), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task An_owned_empty_session_is_a_real_empty_page_not_not_found()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, userId, "Empty");

        using var scope = _fixture.BeginScope();
        var page = await scope.Resolve<ISessionRunMetadataPageReader>().ReadAsync(Request(teamId, SessionSelector(sessionId)), CancellationToken.None);

        page.ShouldNotBeNull();
        page!.MembershipHeadRunNumber.ShouldBe(0);
        page.Items.ShouldBeEmpty();
        page.Omitted.ShouldBe(new SessionRunMetadataOmission { Older = false, Newer = false });
        page.Continuation.OlderCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Unbounded_persisted_text_is_projected_as_typed_utf8_bounded_metadata_without_loading_the_body()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, userId, "Bounded fields");
        var run = await SeedRunAsync(new RunSeed(teamId, sessionId, 1, null, WorkflowRunStatus.Failure));
        var error = new string('界', 800_000);
        var sourceType = string.Concat(Enumerable.Repeat("🧠", 100));
        var projectionKind = new string('模', 100);
        var rerunFromNodeId = new string('n', 600);
        await SetUnboundedMetadataAsync(run.RunId, error, sourceType, projectionKind, rerunFromNodeId);
        var recorder = new ReadCommandRecorder();

        SessionRunMetadataPage page;
        using (var scope = ReadScope(recorder))
            page = (await scope.Resolve<ISessionRunMetadataPageReader>().ReadAsync(Request(teamId, SessionSelector(sessionId)), CancellationToken.None))!;

        var item = page.Items.ShouldHaveSingleItem();
        AssertBounded(item.Error, error, SessionRunMetadataPageRequest.MaximumErrorBytes);
        AssertBounded(item.SourceType, sourceType, SessionRunMetadataPageRequest.MaximumClassifierBytes);
        AssertBounded(item.ProjectionKind, projectionKind, SessionRunMetadataPageRequest.MaximumClassifierBytes);
        AssertBounded(item.RerunFromNodeId, rerunFromNodeId, SessionRunMetadataPageRequest.MaximumNodeIdBytes);
        var command = recorder.Commands.ShouldHaveSingleItem();
        command.ShouldContain("left(r.error, @error_prefix_characters)", Case.Insensitive);
        command.ShouldContain("octet_length(r.error)", Case.Insensitive);
        command.ShouldNotContain("r.error AS error", Case.Insensitive);
        command.ShouldContain("left(r.source_type, @classifier_prefix_characters)", Case.Insensitive);
        command.ShouldContain("left(r.projection_kind, @classifier_prefix_characters)", Case.Insensitive);
        command.ShouldContain("left(r.rerun_from_node_id, @node_id_prefix_characters)", Case.Insensitive);
    }

    [Fact]
    public async Task Invalid_or_cross_selector_cursors_fail_before_any_database_read()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, userId, "Invalid cursor");
        var recorder = new ReadCommandRecorder();
        using var scope = ReadScope(recorder);
        var reader = scope.Resolve<ISessionRunMetadataPageReader>();

        await Should.ThrowAsync<SessionRunMetadataPageRequestException>(() => reader.ReadAsync(Request(teamId, SessionSelector(sessionId), SessionRunMetadataPageDirection.Older, new string('a', SessionRunMetadataPageRequest.MaximumCursorLength + 1)), CancellationToken.None));
        var otherSession = Guid.NewGuid();
        var wrong = new SessionRunMetadataCursor(teamId, otherSession, null, MembershipHeadRunNumber: 10, BeforeRunNumber: 5).Encode();
        await Should.ThrowAsync<SessionRunMetadataPageRequestException>(() => reader.ReadAsync(Request(teamId, SessionSelector(sessionId), SessionRunMetadataPageDirection.Older, wrong), CancellationToken.None));
        await Should.ThrowAsync<SessionRunMetadataPageRequestException>(() => reader.ReadAsync(Request(teamId, SessionSelector(sessionId), SessionRunMetadataPageDirection.Tail, cursor: null, limit: SessionRunMetadataPageRequest.MaximumLimit + 1), CancellationToken.None));

        recorder.Commands.ShouldBeEmpty("invalid identity/cursor/limit contracts fail closed before admission SQL");
    }

    [Fact]
    public async Task Ten_thousand_members_use_the_session_run_number_index_without_scan_or_sort()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, userId, "Large lineage");
        await BulkSeedRunsAsync(teamId, sessionId, userId, 10_050);

        using (var scope = _fixture.BeginScope())
        {
            var page = (await scope.Resolve<ISessionRunMetadataPageReader>().ReadAsync(Request(teamId, SessionSelector(sessionId)), CancellationToken.None))!;
            page.Items.Count.ShouldBe(SessionRunMetadataPageRequest.DefaultLimit);
            page.Omitted.Older.ShouldBeTrue();
        }

        var plan = await ExplainPageAccessAsync(teamId, sessionId);

        plan.ShouldContain("idx_workflow_run_session_run_number", Case.Sensitive);
        plan.ShouldNotContain("Seq Scan on workflow_run", Case.Sensitive);
        plan.ShouldNotContain("Sort", Case.Sensitive, plan);
    }

    private ILifetimeScope ReadScope(DbCommandInterceptor interceptor) => _fixture.BeginScope(builder =>
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options;
        builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    });

    private async Task<Guid> SeedSessionAsync(Guid teamId, Guid userId, string title)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.WorkSession.Add(new WorkSession
        {
            Id = id, TeamId = teamId, Title = title, Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open,
            LastTurnIndex = 1, LastActivityAt = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedBy = userId,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<RunFact> SeedRunAsync(RunSeed seed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var request = RequestEntity(seed.TeamId);
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(), TeamId = seed.TeamId, RunRequestId = request.Id,
            SourceType = seed.TurnIndex.HasValue ? WorkflowRunSourceTypes.Snapshot : WorkflowRunSourceTypes.Rerun,
            Status = seed.Status, SessionId = seed.SessionId, SessionTurnIndex = seed.TurnIndex, RootRunId = seed.RootRunId,
            OutputsJson = "{}", CreatedDate = DateTimeOffset.UtcNow, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        };
        db.WorkflowRunRequest.Add(request);
        db.WorkflowRun.Add(run);
        await db.SaveChangesAsync();
        return new RunFact(run.Id, run.RunNumber);
    }

    private async Task BulkSeedRunsAsync(Guid teamId, Guid sessionId, Guid userId, int count)
    {
        var request = RequestEntity(teamId);
        using (var scope = _fixture.BeginScope())
        {
            scope.Resolve<CodeSpaceDbContext>().WorkflowRunRequest.Add(request);
            await scope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
        }

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO workflow_run
                (id, workflow_id, workflow_version, status, started_at, completed_at, created_date, created_by,
                 last_modified_date, last_modified_by, outputs_jsonb, release_hash_at_run, team_id, run_request_id,
                 source_type, session_id, session_turn_index)
            SELECT gen_random_uuid(), NULL, NULL, 'Success', now(), now(), now(), @user_id,
                   now(), @user_id, '{}'::jsonb, '', @team_id, @request_id, 'snapshot', @session_id, value
            FROM generate_series(1, @count) AS value
            """, connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("team_id", teamId);
        command.Parameters.AddWithValue("request_id", request.Id);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE workflow_run", connection);
        await analyze.ExecuteNonQueryAsync();
    }

    private async Task SetUnboundedMetadataAsync(Guid runId, string error, string sourceType, string projectionKind, string rerunFromNodeId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE workflow_run
            SET error = @error, source_type = @source_type, projection_kind = @projection_kind, rerun_from_node_id = @rerun_from_node_id
            WHERE id = @run_id
            """, connection);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("source_type", sourceType);
        command.Parameters.AddWithValue("projection_kind", projectionKind);
        command.Parameters.AddWithValue("rerun_from_node_id", rerunFromNodeId);
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    private async Task SetRunStatusAsync(Guid runId, WorkflowRunStatus status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(candidate => candidate.Id == runId);
        run.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task<string> ExplainPageAccessAsync(Guid teamId, Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (COSTS OFF)\n" + SessionRunMetadataPageReader.PageSql, connection);
        command.Parameters.AddWithValue("team_id", teamId);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.Add(new NpgsqlParameter("run_anchor_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("cursor_session_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = DBNull.Value });
        command.Parameters.AddWithValue("membership_head_run_number", 0L);
        command.Parameters.AddWithValue("before_run_number", 0L);
        command.Parameters.AddWithValue("take", SessionRunMetadataPageRequest.DefaultLimit + 1);
        command.Parameters.AddWithValue("child_source", WorkflowRunSourceTypes.ChildWorkflow);
        command.Parameters.AddWithValue("classifier_prefix_characters", SessionRunMetadataPageRequest.MaximumClassifierBytes);
        command.Parameters.AddWithValue("node_id_prefix_characters", SessionRunMetadataPageRequest.MaximumNodeIdBytes);
        command.Parameters.AddWithValue("error_prefix_characters", SessionRunMetadataPageRequest.MaximumErrorBytes);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private static WorkflowRunRequest RequestEntity(Guid teamId) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user", ActorId = SystemUsers.SeederId,
        NormalizedPayloadJson = "{}", RequestMetadataJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
        ReceivedAt = DateTimeOffset.UtcNow, VerifiedAt = DateTimeOffset.UtcNow, NormalizedAt = DateTimeOffset.UtcNow,
    };

    private static SessionRunMetadataSelector SessionSelector(Guid sessionId) => new() { Kind = SessionRunMetadataSelectorKind.Session, SessionId = sessionId };
    private static SessionRunMetadataSelector AnchorSelector(Guid runId) => new() { Kind = SessionRunMetadataSelectorKind.RunAnchor, RunAnchorId = runId };
    private static SessionRunMetadataPageRequest Request(Guid teamId, SessionRunMetadataSelector selector, SessionRunMetadataPageDirection direction = SessionRunMetadataPageDirection.Tail, string? cursor = null, int limit = SessionRunMetadataPageRequest.DefaultLimit) =>
        new() { TeamId = teamId, Selector = selector, Direction = direction, Cursor = cursor, Limit = limit };

    private static void AssertBounded(SessionRunMetadataText actual, string original, int maximumBytes)
    {
        actual.State.ShouldBe(SessionRunMetadataTextState.Truncated);
        actual.SizeBytes.ShouldBe(Encoding.UTF8.GetByteCount(original));
        actual.Text.ShouldNotBeNull();
        Encoding.UTF8.GetByteCount(actual.Text!).ShouldBeLessThanOrEqualTo(maximumBytes);
        actual.Text.EnumerateRunes().ShouldNotBeEmpty();
        actual.Text.ShouldBe(original[..actual.Text.Length]);
    }

    private sealed record RunSeed(Guid TeamId, Guid SessionId, int? TurnIndex, Guid? RootRunId, WorkflowRunStatus Status);
    private sealed record RunFact(Guid RunId, long RunNumber);

    private sealed class ReadCommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
