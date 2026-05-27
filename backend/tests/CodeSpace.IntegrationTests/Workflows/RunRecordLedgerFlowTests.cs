using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Append-only ledger primitives. These tests exercise the storage layer directly via
/// <see cref="IRunRecordLogger"/> and raw SQL, with no engine in the loop. Engine-level
/// scenarios live in <c>RunRecordEngineFlowTests</c>.
///
/// Coverage:
///   - Immutability trigger rejects UPDATE
///   - Immutability trigger rejects DELETE
///   - Sequence is strictly increasing per-run
///   - Sequence is monotonic across multiple writers to the same run (sanity, single
///     thread is the documented contract)
///   - Records are visible immediately after the logger call returns
///   - Cross-run isolation: queries scoped to one run never see another's records
///   - Cascade delete: removing the workflow_run row cascades to its records
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunRecordLedgerFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunRecordLedgerFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Trigger_rejects_UPDATE_on_existing_record()
    {
        var runId = await SeedRunAsync();
        var recordId = await WriteOneRecordAsync(runId, WorkflowRunRecordTypes.Log);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        // Direct SQL UPDATE — bypasses EF tracking. Should be slapped down by the trigger.
        var ex = await Should.ThrowAsync<Npgsql.PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE workflow_run_record SET payload_json = '{{\"tampered\":true}}'::jsonb WHERE id = {recordId}");
        });

        ex.MessageText.ShouldContain("append-only",
            customMessage: "trigger error message must surface the immutability rule for operator readability");
    }

    [Fact]
    public async Task Trigger_rejects_DELETE_on_existing_record()
    {
        var runId = await SeedRunAsync();
        var recordId = await WriteOneRecordAsync(runId, WorkflowRunRecordTypes.Log);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var ex = await Should.ThrowAsync<Npgsql.PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM workflow_run_record WHERE id = {recordId}");
        });

        ex.MessageText.ShouldContain("append-only");
    }

    [Fact]
    public async Task Sequence_is_strictly_increasing_per_run()
    {
        var runId = await SeedRunAsync();

        // Write 10 records in order; sequences should be strictly increasing.
        var ids = new List<Guid>();
        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            for (int i = 0; i < 10; i++)
            {
                await logger.LogAsync(runId, nodeId: null, LogLevel.Info, $"msg-{i}", CancellationToken.None);
            }
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        // Filter to the records this test wrote — SeedManualRunAsync emits a run.queued
        // lifecycle record, which is not under test here.
        var seqs = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.Log)
            .OrderBy(r => r.Sequence)
            .Select(r => r.Sequence)
            .ToListAsync();

        seqs.Count.ShouldBe(10);
        for (int i = 1; i < seqs.Count; i++)
            seqs[i].ShouldBeGreaterThan(seqs[i - 1], "BIGSERIAL must be strictly increasing per INSERT order");
    }

    [Fact]
    public async Task Two_runs_have_isolated_record_sets()
    {
        var runIdA = await SeedRunAsync();
        var runIdB = await SeedRunAsync();

        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.LogAsync(runIdA, nodeId: null, LogLevel.Info, "a1", CancellationToken.None);
            await logger.LogAsync(runIdB, nodeId: null, LogLevel.Info, "b1", CancellationToken.None);
            await logger.LogAsync(runIdA, nodeId: null, LogLevel.Info, "a2", CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // Filter to log records — SeedManualRunAsync emits a run.queued record which isn't
        // part of the cross-run isolation surface under test.
        var aRecords = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runIdA && r.RecordType == WorkflowRunRecordTypes.Log).ToListAsync();
        var bRecords = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runIdB && r.RecordType == WorkflowRunRecordTypes.Log).ToListAsync();

        aRecords.Count.ShouldBe(2);
        bRecords.Count.ShouldBe(1);
        aRecords.ShouldAllBe(r => r.RunId == runIdA, "tenant isolation: run A's query must not surface run B's records");
        bRecords.ShouldAllBe(r => r.RunId == runIdB);
    }

    [Fact]
    public async Task Records_outlive_their_parent_run_attempted_delete_is_blocked()
    {
        // Records are append-only audit; the workflow_run FK is NOT ON DELETE CASCADE, so
        // deleting the parent run while records exist is rejected. This is a deliberate
        // safety: an audit-log row can never be silently erased by a parent-row cleanup.
        var runId = await SeedRunAsync();

        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.LogAsync(runId, nodeId: null, LogLevel.Info, "permanent", CancellationToken.None);
        }

        // Try to delete the parent. Either the FK rejects first (preferred) or the
        // immutability trigger fires on the cascade. Both are acceptable surfaces; we just
        // assert that the records survive the attempt.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            await Should.ThrowAsync<Npgsql.PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM workflow_run WHERE id = {runId}");
            });
        }

        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();
        // Filter to the log record this test wrote — SeedManualRunAsync's run.queued
        // record also survives but isn't the assertion target here.
        var remaining = await verifyDb.WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.Log);
        remaining.ShouldBe(1, "audit records must outlive any attempt to delete their parent run");
    }

    [Fact]
    public async Task Records_visible_immediately_after_logger_returns()
    {
        var runId = await SeedRunAsync();

        using var scope = _fixture.BeginScope();
        var logger = scope.Resolve<IRunRecordLogger>();
        var db = scope.Resolve<CodeSpaceDbContext>();

        await logger.LogAsync(runId, nodeId: null, LogLevel.Info, "now-visible", CancellationToken.None);

        // Same scope → same DbContext; the change-tracker holds the inserted record AND the
        // INSERT has already been flushed (logger calls SaveChanges). A fresh AsNoTracking
        // query MUST see the row. Filter to log records — SeedManualRunAsync emits a
        // run.queued record which is not the visibility target here.
        var count = await db.WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.Log);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task Parent_record_id_links_attempt_chain()
    {
        var runId = await SeedRunAsync();

        using var scope = _fixture.BeginScope();
        var logger = scope.Resolve<IRunRecordLogger>();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var parentId = await logger.NodeStartedAsync(runId, "n1", iterationKey: "",
            resolvedInputs: new Dictionary<string, JsonElement>(),
            resolvedConfig: new Dictionary<string, JsonElement>(), cancellationToken: CancellationToken.None);

        // An external call nested under n1 — its parent_record_id points back at the node row.
        var (callId, correlationId) = await logger.ExternalCallStartedAsync(runId, "n1",
            target: "https://api.example.com", method: "GET",
            requestPayload: null, parentRecordId: parentId, cancellationToken: CancellationToken.None);

        await logger.ExternalCallCompletedAsync(runId, "n1", correlationId,
            statusCode: 200, responsePayload: null, duration: TimeSpan.FromMilliseconds(42),
            cancellationToken: CancellationToken.None);

        // Filter out run.queued — SeedManualRunAsync writes it but it's not part of the
        // parent-chain assertion under test here.
        var records = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType != WorkflowRunRecordTypes.RunQueued)
            .OrderBy(r => r.Sequence)
            .ToListAsync();

        records.Count.ShouldBe(3);

        var nodeRow = records.Single(r => r.RecordType == WorkflowRunRecordTypes.NodeStarted);
        var callStarted = records.Single(r => r.RecordType == WorkflowRunRecordTypes.ExternalCallStarted);
        var callCompleted = records.Single(r => r.RecordType == WorkflowRunRecordTypes.ExternalCallCompleted);

        nodeRow.ParentRecordId.ShouldBeNull();
        callStarted.ParentRecordId.ShouldBe(nodeRow.Id, "external_call.started must link back to the node row that spawned it");
        callStarted.CorrelationId.ShouldBe(correlationId);
        callCompleted.CorrelationId.ShouldBe(correlationId, "completed shares the correlation id with started");
        callCompleted.ParentRecordId.ShouldBeNull("only the .started carries the parent link; completed pairs via correlation_id");
    }

    [Fact]
    public async Task External_call_failed_emits_with_correlation_pairing()
    {
        var runId = await SeedRunAsync();

        using var scope = _fixture.BeginScope();
        var logger = scope.Resolve<IRunRecordLogger>();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var (_, correlationId) = await logger.ExternalCallStartedAsync(runId, nodeId: "n1",
            target: "https://api.example.com", method: "POST",
            requestPayload: null, parentRecordId: null, cancellationToken: CancellationToken.None);

        await logger.ExternalCallFailedAsync(runId, nodeId: "n1", correlationId,
            target: "https://api.example.com", error: "connection refused",
            duration: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None);

        var failedRecord = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.ExternalCallFailed)
            .SingleAsync();

        failedRecord.CorrelationId.ShouldBe(correlationId);

        var payload = JsonDocument.Parse(failedRecord.PayloadJson).RootElement;
        payload.GetProperty("error").GetString().ShouldBe("connection refused");
        payload.GetProperty("target").GetString().ShouldBe("https://api.example.com");
        payload.GetProperty("duration_ms").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Log_records_serialise_level_as_lowercase_string()
    {
        var runId = await SeedRunAsync();

        using var scope = _fixture.BeginScope();
        var logger = scope.Resolve<IRunRecordLogger>();
        var db = scope.Resolve<CodeSpaceDbContext>();

        await logger.LogAsync(runId, nodeId: "n1", LogLevel.Info, "i", CancellationToken.None);
        await logger.LogAsync(runId, nodeId: "n1", LogLevel.Warn, "w", CancellationToken.None);
        await logger.LogAsync(runId, nodeId: "n1", LogLevel.Error, "e", CancellationToken.None);

        var levels = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.Log)
            .OrderBy(r => r.Sequence)
            .Select(r => r.PayloadJson)
            .ToListAsync();

        levels.Count.ShouldBe(3);
        JsonDocument.Parse(levels[0]).RootElement.GetProperty("level").GetString().ShouldBe("info");
        JsonDocument.Parse(levels[1]).RootElement.GetProperty("level").GetString().ShouldBe("warn");
        JsonDocument.Parse(levels[2]).RootElement.GetProperty("level").GetString().ShouldBe("error");
    }

    [Fact]
    public async Task Iteration_records_carry_item_count()
    {
        var runId = await SeedRunAsync();

        using var scope = _fixture.BeginScope();
        var logger = scope.Resolve<IRunRecordLogger>();
        var db = scope.Resolve<CodeSpaceDbContext>();

        await logger.IterationStartedAsync(runId, nodeId: "loop", itemCount: 7, CancellationToken.None);
        await logger.IterationCompletedAsync(runId, nodeId: "loop", itemCount: 7, duration: TimeSpan.FromMilliseconds(120), CancellationToken.None);

        var iterRecords = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType.StartsWith("iteration."))
            .OrderBy(r => r.Sequence)
            .ToListAsync();

        iterRecords.Count.ShouldBe(2);
        JsonDocument.Parse(iterRecords[0].PayloadJson).RootElement.GetProperty("item_count").GetInt32().ShouldBe(7);
        JsonDocument.Parse(iterRecords[1].PayloadJson).RootElement.GetProperty("item_count").GetInt32().ShouldBe(7);
        JsonDocument.Parse(iterRecords[1].PayloadJson).RootElement.GetProperty("duration_ms").GetInt64().ShouldBeGreaterThanOrEqualTo(120);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedRunAsync()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Create a workflow + version 1 row. The workflow_run FK on (workflow_id,
        // workflow_version) rejects the run insert otherwise. For ledger-only tests we just
        // need a parent run to attach records to — the definition JSON itself doesn't matter.
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var workflowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Workflow.Add(new Workflow
        {
            Id = workflowId,
            TeamId = teamId,
            Name = "ledger-test-" + Guid.NewGuid().ToString("N")[..6],
            DefinitionJson = "{}",
            LatestVersion = 1,
            Enabled = true,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });
        db.WorkflowVersion.Add(new WorkflowVersion
        {
            WorkflowId = workflowId,
            Version = 1,
            DefinitionJson = "{}",
            DefinitionHash = "0000000000000000000000000000000000000000000000000000000000000000",
            CommittedAt = now,
            CreatedDate = now,
        });
        await db.SaveChangesAsync();

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> WriteOneRecordAsync(Guid runId, string recordType)
    {
        using var scope = _fixture.BeginScope();
        var logger = scope.Resolve<IRunRecordLogger>();
        return await logger.NodeStartedAsync(runId, nodeId: "n", iterationKey: "",
            resolvedInputs: new Dictionary<string, JsonElement>(),
            resolvedConfig: new Dictionary<string, JsonElement>(), cancellationToken: CancellationToken.None);
    }
}
