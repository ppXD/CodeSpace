using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Trace;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="IRunRecordReader"/> from DI): the Trace tab's raw ledger read.
/// Unlike the narrative timeline (which drops log / scope / variables / external-call noise), the reader returns EVERY
/// <c>workflow_run_record</c> row in Sequence order with its raw payload verbatim. Team-scoped via the run precheck —
/// a foreign / absent run resolves to null (404-conflate, fail-closed).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RunRecordsReaderFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunRecordsReaderFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Reads_every_record_type_unfiltered_in_sequence_order_with_raw_payloads()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);

        var t = DateTimeOffset.UtcNow;
        await SeedRecordsAsync(runId,
            (WorkflowRunRecordTypes.RunStarted, null, "{}", t),
            (WorkflowRunRecordTypes.ScopeResolved, null, """{"repos":2}""", t.AddSeconds(1)),     // narrative DROPS this
            (WorkflowRunRecordTypes.Log, "code", """{"message":"hi"}""", t.AddSeconds(2)),         // narrative DROPS this
            (WorkflowRunRecordTypes.NodeStarted, "code", "{}", t.AddSeconds(3)),
            (WorkflowRunRecordTypes.RunCompleted, null, "{}", t.AddSeconds(4)));

        var result = await ReadAsync(userId, teamId, runId);

        result.ShouldNotBeNull();
        result!.RunStatus.ShouldBe(nameof(WorkflowRunStatus.Failure));
        result.Records.Select(r => r.RecordType).ShouldBe(new[]
        {
            WorkflowRunRecordTypes.RunStarted, WorkflowRunRecordTypes.ScopeResolved, WorkflowRunRecordTypes.Log,
            WorkflowRunRecordTypes.NodeStarted, WorkflowRunRecordTypes.RunCompleted,
        }, "the Trace reader is UNFILTERED — even the scope/log records the narrative timeline drops are present, in Sequence order");

        var scope = result.Records.Single(r => r.RecordType == WorkflowRunRecordTypes.ScopeResolved);
        // The raw payload is carried through (not a derived narrative title). It's a jsonb column, so Postgres
        // normalizes whitespace — assert semantically (parse it), not byte-for-byte.
        JsonDocument.Parse(scope.PayloadJson).RootElement.GetProperty("repos").GetInt32().ShouldBe(2);
        scope.NodeId.ShouldBeNull();

        var log = result.Records.Single(r => r.RecordType == WorkflowRunRecordTypes.Log);
        log.NodeId.ShouldBe("code");
    }

    [Fact]
    public async Task A_foreign_run_resolves_to_null()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var result = await ReadAsync(userId, teamId, Guid.NewGuid());

        result.ShouldBeNull("a run that isn't the team's resolves to null — 404-conflate, no existence leak");
    }

    [Fact]
    public async Task A_run_with_no_records_returns_an_empty_list_not_null()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);   // a real team run, but no ledger rows seeded

        var result = await ReadAsync(userId, teamId, runId);

        result.ShouldNotBeNull("the run is the team's, so it resolves — distinct from a foreign run's null");
        result!.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bounded_tail_older_and_newer_pages_cover_a_large_ledger_without_overlap_or_gaps()
    {
        const int total = 10_003;
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        await SeedManyRecordsAsync(runId, total);

        var tail = await ReadPageAsync(userId, teamId, new RunRecordPageRequest(runId, teamId, null, null, 500));
        tail.ShouldNotBeNull();
        tail!.Mode.ShouldBe(RunRecordPageModes.Tail);
        tail.Records.Count.ShouldBe(500);
        tail.Records.Select(row => row.Sequence).ShouldBeInOrder();
        tail.NextBeforeSequence.ShouldBe(tail.Records[0].Sequence);
        tail.NextAfterSequence.ShouldBeNull();

        var seen = tail.Records.Select(row => row.Sequence).ToHashSet();
        var before = tail.NextBeforeSequence;
        while (before != null)
        {
            var page = await ReadPageAsync(userId, teamId, new RunRecordPageRequest(runId, teamId, before, null, 500));
            page.ShouldNotBeNull();
            page!.Mode.ShouldBe(RunRecordPageModes.Older);
            page.Records.Select(row => row.Sequence).ShouldBeInOrder();
            page.Records.All(row => row.Sequence < before).ShouldBeTrue();
            foreach (var row in page.Records) seen.Add(row.Sequence).ShouldBeTrue("keyset pages must never overlap");
            before = page.NextBeforeSequence;
        }

        seen.Count.ShouldBe(total, "walking bounded older pages reconstructs the full append-only ledger");

        var priorHead = tail.Records[^1].Sequence;
        await SeedManyRecordsAsync(runId, 3);

        var newer1 = await ReadPageAsync(userId, teamId, new RunRecordPageRequest(runId, teamId, null, priorHead, 2));
        newer1.ShouldNotBeNull();
        newer1!.Mode.ShouldBe(RunRecordPageModes.Newer);
        newer1.Records.Count.ShouldBe(2);
        newer1.Records.Select(row => row.Sequence).ShouldBeInOrder();
        newer1.NextAfterSequence.ShouldBe(newer1.Records[^1].Sequence);
        newer1.NextBeforeSequence.ShouldBeNull();

        var newer2 = await ReadPageAsync(userId, teamId, new RunRecordPageRequest(runId, teamId, null, newer1.NextAfterSequence, 2));
        newer2.ShouldNotBeNull();
        newer2!.Records.Count.ShouldBe(1);
        newer2.NextAfterSequence.ShouldBeNull();
        newer1.Records.Concat(newer2.Records).Select(row => row.Sequence).ShouldBeInOrder();
    }

    [Fact]
    public async Task Bounded_page_conflates_a_foreign_run_with_missing()
    {
        var (ownerTeam, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(ownerTeam);
        await SeedManyRecordsAsync(runId, 1);
        var (foreignTeam, foreignUser) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var result = await ReadPageAsync(foreignUser, foreignTeam, new RunRecordPageRequest(runId, foreignTeam, null, null, 10));

        result.ShouldBeNull();
    }

    private async Task<RunRecordsResponse?> ReadAsync(Guid userId, Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IRunRecordReader>().ReadAsync(runId, teamId, CancellationToken.None);
    }

    private async Task<RunRecordPageResponse?> ReadPageAsync(Guid userId, Guid teamId, RunRecordPageRequest request)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IRunRecordPageReader>().ReadAsync(request, CancellationToken.None);
    }

    private async Task SeedManyRecordsAsync(Guid runId, int count)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO workflow_run_record (id, run_id, record_type, iteration_key, occurred_at, payload_json)
            SELECT gen_random_uuid(), {{runId}}, 'test.record', '', NOW(), jsonb_build_object('ordinal', n)
              FROM generate_series(1, {{count}}) AS n
            """);
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
            db.WorkflowRunRecord.Add(new WorkflowRunRecord
            {
                Id = Guid.NewGuid(), RunId = runId, RecordType = type, NodeId = nodeId, OccurredAt = at, PayloadJson = payload,
            });

            // Save per row so the DB-assigned BIGSERIAL Sequence increments in add-order — mirroring production, where
            // the engine writes records one at a time. A single batched SaveChanges does NOT guarantee Sequence order.
            await db.SaveChangesAsync();
        }
    }
}
