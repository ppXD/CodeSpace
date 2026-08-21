using System.Data.Common;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>Real-Postgres contract for the narrow Room/Journal session skeleton hot path.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SessionSkeletonReaderFlowTests
{
    private const int BaggageBytes = 2 * 1024 * 1024;
    private readonly PostgresFixture _fixture;

    public SessionSkeletonReaderFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task By_session_reads_only_leaf_metadata_in_one_command_and_preserves_the_effective_attempt()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, userId, "Narrow skeleton");
        var now = DateTimeOffset.UtcNow;
        var goal = "Inspect the deployment topology";
        var expectedResult = new string('r', 600) + "…";

        var original = await SeedRunAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, WorkflowRunStatus.Failure, now.AddMinutes(-3),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["goal"] = goal, ["Goal"] = "legacy Pascal must not override", ["unrelated"] = new string('p', BaggageBytes) }), "{}");
        var winner = await SeedRunAsync(teamId, sessionId, turnIndex: null, rootRunId: original, WorkflowRunStatus.Success, now.AddMinutes(-2), "{}",
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["summary"] = new string('r', 605), ["Summary"] = "legacy Pascal must not override", ["unrelated"] = new string('o', BaggageBytes) }));
        var laterFailure = await SeedRunAsync(teamId, sessionId, turnIndex: null, rootRunId: original, WorkflowRunStatus.Failure, now.AddMinutes(-1), "{}", "{}");

        var recorder = new ReadCommandRecorder();
        using var scope = ReadScope(recorder);
        var skeleton = await scope.Resolve<ISessionSkeletonReader>().GetBySessionAsync(sessionId, teamId, CancellationToken.None);

        skeleton.ShouldNotBeNull();
        var turn = skeleton!.Turns.ShouldHaveSingleItem();
        turn.UserMessage.ShouldBe(goal, "the exact lowercase goal leaf crosses without carrying the 2 MiB root");
        turn.RunId.ShouldBe(winner, "the newest success remains effective even when a newer attempt failed");
        turn.RunStatus.ShouldBe(WorkflowRunStatus.Success);
        turn.Result.ShouldBe(expectedResult, "SQL carries at most 601 result codepoints and the established CLR Clip(600) adds the honest ellipsis");
        turn.Attempts!.Select(attempt => attempt.RunId).ShouldBe(new[] { original, winner, laterFailure });
        turn.Attempts.Single(attempt => attempt.IsLatest).RunId.ShouldBe(winner);
        turn.ProducedBranch.ShouldBeNull();
        turn.RepositoryResults.ShouldBeNull();

        var sql = recorder.Commands.ShouldHaveSingleItem("one skeleton call is one database command");
        sql.ShouldContain("session-skeleton", Case.Insensitive);
        sql.ShouldNotContain("scope_repository_ids", Case.Insensitive);
        sql.ShouldNotContain("repository_results", Case.Insensitive);
        sql.ShouldNotContain("publish_manifest", Case.Insensitive);
        sql.ShouldNotContain("branch", Case.Insensitive);
        sql.ShouldNotContain("r.outputs_jsonb AS", Case.Insensitive, "the root OutputsJson must never cross the database/CLR boundary");
        sql.ShouldNotContain("q.normalized_payload_json AS", Case.Insensitive, "the root normalized payload must never cross the database/CLR boundary");
    }

    [Fact]
    public async Task Leaf_key_casing_and_type_fallback_remain_byte_identical_to_SessionTurnText()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, userId, "Exact leaf semantics");
        await SeedRunAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, WorkflowRunStatus.Success, DateTimeOffset.UtcNow,
            """{"goal":42,"Goal":"Pascal goal is not authoritative"}""",
            """{"summary":42,"Summary":"Pascal summary is not authoritative","combined":"lowercase fallback","Combined":"Pascal combined is not authoritative"}""");

        using var scope = _fixture.BeginScope();
        var turn = (await scope.Resolve<ISessionSkeletonReader>().GetBySessionAsync(sessionId, teamId, CancellationToken.None))!.Turns.ShouldHaveSingleItem();

        turn.UserMessage.ShouldBeNull("the established reader ignores Pascal-only goal and non-string lowercase goal");
        turn.Result.ShouldBe("lowercase fallback", "a wrong-type lowercase summary continues to the lowercase combined field; Pascal siblings are ignored");
    }

    [Fact]
    public async Task By_run_and_by_session_are_team_exact_and_differ_only_by_the_anchor()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, userId, "Anchor");
        var now = DateTimeOffset.UtcNow;
        var original = await SeedRunAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, WorkflowRunStatus.Failure, now.AddMinutes(-1), JsonSerializer.Serialize(new { goal = "Fix it" }), "{}");
        var winner = await SeedRunAsync(teamId, sessionId, turnIndex: null, rootRunId: original, WorkflowRunStatus.Success, now, "{}", JsonSerializer.Serialize(new { combined = "Fixed" }));

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<ISessionSkeletonReader>();
        var bySession = await reader.GetBySessionAsync(sessionId, teamId, CancellationToken.None);
        var byRun = await reader.GetByRunAsync(winner, teamId, CancellationToken.None);

        JsonSerializer.Serialize(byRun).ShouldBe(JsonSerializer.Serialize(bySession! with { AnchorTurnIndex = 1 }), "by-run and by-session carry byte-identical header/turn metadata; only the anchor differs");
        (await reader.GetBySessionAsync(sessionId, foreignTeamId, CancellationToken.None)).ShouldBeNull();
        (await reader.GetByRunAsync(winner, foreignTeamId, CancellationToken.None)).ShouldBeNull();
        (await reader.GetByRunAsync(Guid.NewGuid(), teamId, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Room_and_journal_consume_one_skeleton_command_each_with_matching_turn_wire()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, userId, "Projection cutover");
        var runId = await SeedRunAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, WorkflowRunStatus.Success, DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(new { goal = "Explain the system" }), JsonSerializer.Serialize(new { reason = "System explained" }));

        var recorder = new ReadCommandRecorder();
        using var scope = ReadScope(recorder);
        var room = await scope.Resolve<IRoomProjector>().ProjectByRunAsync(runId, teamId, CancellationToken.None);
        var journal = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(runId, teamId, CancellationToken.None);

        var roomTurn = room!.Blocks.OfType<AssistantTurnBlock>().ShouldHaveSingleItem();
        var journalTurn = journal!.Turns.ShouldHaveSingleItem();
        var roomWire = JsonSerializer.Serialize(new { room.SessionId, room.Title, room.Kind, SessionStatus = room.Status, roomTurn.TurnIndex, roomTurn.TurnRunId, roomTurn.RunId, RunStatus = roomTurn.Status, UserMessage = room.Blocks.OfType<UserMessageBlock>().ShouldHaveSingleItem().Text, Result = roomTurn.Summary });
        var journalWire = JsonSerializer.Serialize(new { journal.SessionId, journal.Title, journal.Kind, SessionStatus = journal.Status, journalTurn.TurnIndex, journalTurn.TurnRunId, journalTurn.RunId, RunStatus = journalTurn.Status, UserMessage = journalTurn.UserMessage, Result = journalTurn.Summary });

        journalWire.ShouldBe(roomWire, "the two production consumers receive the exact same narrow turn/header projection");
        recorder.Commands.Count(command => command.Contains("session-skeleton", StringComparison.OrdinalIgnoreCase)).ShouldBe(2, "Room and Journal each issue one skeleton command, never the legacy multi-query detail fold");
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
        var sessionId = Guid.NewGuid();
        db.WorkSession.Add(new WorkSession
        {
            Id = sessionId, TeamId = teamId, Title = title, Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open,
            LastTurnIndex = 1, LastActivityAt = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedBy = userId,
        });
        await db.SaveChangesAsync();
        return sessionId;
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid sessionId, int? turnIndex, Guid? rootRunId, WorkflowRunStatus status, DateTimeOffset createdAt, string payload, string outputs)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = turnIndex is null ? WorkflowRunSourceTypes.Rerun : WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = payload, RequestMetadataJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = createdAt, VerifiedAt = createdAt, NormalizedAt = createdAt,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = turnIndex is null ? WorkflowRunSourceTypes.Rerun : WorkflowRunSourceTypes.Snapshot,
            Status = status, SessionId = sessionId, SessionTurnIndex = turnIndex, RootRunId = rootRunId, OutputsJson = outputs,
            CreatedDate = createdAt, CompletedAt = status is WorkflowRunStatus.Success or WorkflowRunStatus.Failure ? createdAt.AddSeconds(1) : null,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

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
