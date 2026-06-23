using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks.Timeline;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="RunRecordTimelineSource"/> resolved from DI): the narrative
/// timeline end-to-end at the projector seam — a run's append-only <c>workflow_run_record</c> ledger surfaces through
/// the run-timeline projector as the NARRATIVE-worthy lifecycle events, chronologically merged, with Trace-level
/// noise (log lines) dropped. A foreign / absent run resolves to null (team-scoped, fail-closed).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RunTimelineProjectionFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunTimelineProjectionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Projects_the_run_lifecycle_records_chronologically_dropping_trace_noise()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);

        var t = DateTimeOffset.UtcNow;
        await SeedRecordsAsync(runId,
            (WorkflowRunRecordTypes.RunStarted, null, "{}", t),
            (WorkflowRunRecordTypes.NodeStarted, "code", "{}", t.AddSeconds(1)),
            (WorkflowRunRecordTypes.Log, "code", """{"level":"info","message":"noise"}""", t.AddSeconds(2)),
            (WorkflowRunRecordTypes.NodeFailed, "code", """{"error":"boom"}""", t.AddSeconds(3)),
            (WorkflowRunRecordTypes.RunFailed, null, """{"error":"boom"}""", t.AddSeconds(4)));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        events!.Select(e => e.Title).ShouldBe(new[] { "Run started", "code started", "code failed", "Run failed" },
            "the lifecycle records project chronologically; the log line is Trace-level noise and is dropped");

        var failed = events.Single(e => e.Title == "code failed");
        failed.Severity.ShouldBe(TimelineSeverity.Error);
        failed.Summary.ShouldBe("boom");
        failed.NodeId.ShouldBe("code");
    }

    [Fact]
    public async Task A_foreign_run_resolves_to_null()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var events = await ProjectAsync(userId, teamId, Guid.NewGuid());

        events.ShouldBeNull("a run that isn't the team's resolves to null — 404-conflate, no existence leak");
    }

    private async Task<IReadOnlyList<RunTimelineEvent>?> ProjectAsync(Guid userId, Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IRunTimelineProjector>().ProjectAsync(runId, teamId, CancellationToken.None);
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

    private async Task SeedRecordsAsync(Guid runId, params (string Type, string? NodeId, string Payload, DateTimeOffset At)[] records)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        foreach (var (type, nodeId, payload, at) in records)
        {
            // Sequence is a DB-assigned BIGSERIAL — left unset so insert order (= chronological here) drives it.
            db.WorkflowRunRecord.Add(new WorkflowRunRecord
            {
                Id = Guid.NewGuid(), RunId = runId, RecordType = type, NodeId = nodeId, OccurredAt = at, PayloadJson = payload,
            });
        }

        await db.SaveChangesAsync();
    }
}
