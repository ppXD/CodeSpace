using System.Reflection;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks.Timeline;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL run-record timeline source resolved from DI): the lifecycle source pushes
/// its narrative record-type filter INTO SQL instead of loading the whole ledger and dropping most rows in C#. The
/// default run view walks this per turn on a 2s poll per viewer, and a streamed 30-minute run accumulates thousands of
/// <c>interaction.delta</c> rows, so the difference is the whole ledger's <c>payload_json</c> crossing the wire versus
/// only the narrative slice. The EQUIVALENCE assertion is the load-bearing one: the projected events must stay
/// event-for-event identical to the load-everything-then-filter path, because the Journal and the Room ARE how the
/// operator judges a run — a dropped or reordered event is a regression even though no model is involved.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RunRecordTimelineFilterFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunRecordTimelineFilterFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_sql_filtered_source_projects_exactly_what_loading_every_record_and_filtering_in_memory_did()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var seeded = await SeedEveryRecordTypeAsync(runId);

        var viaSource = await ContributeAsync(userId, teamId, runId);
        var viaLoadEverything = await ProjectEveryRecordAsync(runId);

        viaSource.ShouldBe(viaLoadEverything, "the SQL-filtered projection must be event-for-event identical — same events, same order, same titles / severities / levels / summaries");
        viaLoadEverything.ShouldNotBeEmpty();
        seeded.ShouldBeGreaterThan(viaLoadEverything.Count, "the fixture must actually contain dropped noise (delta / log / scope / external_call), or the equivalence claim is vacuous");
    }

    [Fact]
    public async Task A_dropped_record_types_payload_never_leaves_the_database()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);

        // A streamed run's real shape: one narrative record buried under a wall of interaction.delta rows, each carrying
        // a fat payload. Every returned row is asserted narrative, so no dropped type's payload_json was ever selected.
        await SeedRecordsAsync(runId, (WorkflowRunRecordTypes.RunStarted, "{}"));
        await SeedDeltaFloodAsync(runId, count: 200, payloadFiller: new string('x', 2_000));

        var rows = await LoadNarrativeRowsAsync(runId);

        rows.Select(r => r.RecordType).ShouldBe([WorkflowRunRecordTypes.RunStarted], "only the narrative record crosses the wire — the 200 delta payloads are filtered server-side");
        rows.ShouldAllBe(r => RunRecordTimelineMap.NarrativeRecordTypes.Contains(r.RecordType));
    }

    [Fact]
    public async Task The_pushed_down_record_type_predicate_can_be_served_by_idx_wrr_run_type()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        await SeedRecordsAsync(runId, (WorkflowRunRecordTypes.RunStarted, "{}"));
        await SeedDeltaFloodAsync(runId, count: 500, payloadFiller: "x");

        var plan = await ExplainNarrativeFilterAsync(runId);

        plan.ShouldContain("idx_wrr_run_type", customMessage: $"the (run_id, record_type) predicate must be index-servable — a shape the index can't answer (a function over record_type, a client-evaluated list) sends the scan back to the heap. Plan was:\n{plan}");
    }

    /// <summary>The production source, resolved through DI exactly as the projector fans out to it — the run is team-prechecked upstream, so calling it directly mirrors production.</summary>
    private async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(Guid userId, Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var source = scope.Resolve<IEnumerable<IRunTimelineSource>>().Single(s => s.SourceKey == RunRecordTimelineMap.Key);

        return await source.ContributeAsync(new RunTimelineContext { RunId = runId, TeamId = teamId }, CancellationToken.None);
    }

    /// <summary>
    /// The EQUIVALENCE ORACLE — the pre-change production read, verbatim: load every record for the run in ledger order
    /// as a full entity, then let the map drop the noise in C#. Kept here (not in production) so the new pushed-down
    /// query is measured against the behaviour it replaced, not against itself.
    /// </summary>
    private async Task<IReadOnlyList<RunTimelineEvent>> ProjectEveryRecordAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var records = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId)
            .OrderBy(r => r.Sequence)
            .ToListAsync();

        return RunRecordTimelineMap.Project(records);
    }

    private async Task<List<WorkflowRunRecord>> LoadNarrativeRowsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        return await RunRecordTimelineSource.NarrativeRecordsQuery(db, runId).ToListAsync();
    }

    /// <summary>
    /// EXPLAIN the pushed-down predicate with sequential scans disabled — on a small table a seq scan always wins on
    /// cost, which would prove nothing about whether the index CAN serve the predicate shape. Postgres normalises the
    /// <c>IN (…)</c> list EF emits and the <c>= ANY(array)</c> written here to the same node, so this is the production
    /// predicate. Deliberately no ORDER BY: the FULL query's plan is the planner's choice between this index and
    /// <c>idx_wrr_run_sequence</c> (which supplies the ordering for free), and pinning that choice would be pinning
    /// cost estimates. What must hold is that the predicate itself is index-servable.
    /// </summary>
    private async Task<string> ExplainNarrativeFilterAsync(Guid runId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var seqScanOff = new NpgsqlCommand("SET enable_seqscan = off", connection))
            await seqScanOff.ExecuteNonQueryAsync();

        await using var explain = new NpgsqlCommand("EXPLAIN SELECT payload_json FROM workflow_run_record WHERE run_id = @run AND record_type = ANY(@types)", connection);
        explain.Parameters.AddWithValue("run", runId);
        explain.Parameters.AddWithValue("types", RunRecordTimelineMap.NarrativeRecordTypes.ToArray());

        var lines = new List<string>();

        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));

        return string.Join("\n", lines);
    }

    /// <summary>Seeds one row of EVERY canonical record type (narrative AND noise — delta, log, scope, release, external_call, iteration), plus an unknown plugin type and a repeated RunStarted / RunReplayed so the resume fold is exercised. Returns the row count.</summary>
    private async Task<int> SeedEveryRecordTypeAsync(Guid runId)
    {
        const string richPayload = """{"error":"boom","wait_kind":"Timer","reason":"dead edge","attempt":1,"max_attempts":3,"kind":"llm.complete","model":"m","usage":{"inputTokens":3,"outputTokens":4,"finishReason":"length"}}""";

        var types = AllCanonicalRecordTypes()
            .Concat([WorkflowRunRecordTypes.RunStarted, WorkflowRunRecordTypes.RunReplayed, "plugin.custom_event"])
            .ToList();

        await SeedRecordsAsync(runId, types.Select(t => (t, richPayload)).ToArray());

        return types.Count;
    }

    /// <summary>Every canonical engine-emitted record type, by reflection over the constants — a newly added type is covered without editing this test.</summary>
    private static IReadOnlyList<string> AllCanonicalRecordTypes() =>
        typeof(WorkflowRunRecordTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    private async Task SeedDeltaFloodAsync(Guid runId, int count, string payloadFiller)
    {
        var deltas = Enumerable.Range(0, count)
            .Select(i => (WorkflowRunRecordTypes.InteractionDelta, $$"""{"ordinal":{{i}},"text":"{{payloadFiller}}"}"""))
            .ToArray();

        await SeedRecordsAsync(runId, deltas);
    }

    private async Task SeedRecordsAsync(Guid runId, params (string Type, string Payload)[] records)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var at = DateTimeOffset.UtcNow;

        foreach (var (type, payload) in records)
        {
            // Sequence is a DB-assigned BIGSERIAL — left unset so insert order (= chronological here) drives it.
            db.WorkflowRunRecord.Add(new WorkflowRunRecord
            {
                Id = Guid.NewGuid(), RunId = runId, RecordType = type, NodeId = "code", OccurredAt = at, PayloadJson = payload,
            });
        }

        await db.SaveChangesAsync();
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
}
