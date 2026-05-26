using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using PersistenceEntities = CodeSpace.Core.Persistence.Entities;
using MessageEnums = CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Pins the run-level lifecycle record sequence. Together with the node lifecycle
/// assertions, this gives operators a complete "what happened to this run" timeline backed
/// by tests.
///
/// <para>Why the sequence matters: the run-detail UI's timeline pane reads these records
/// chronologically. If the engine ever emits them in the wrong order (e.g. scope.resolved
/// before release.loaded), the UI shows an impossible sequence and trust in the audit is
/// gone. Tests pin both presence + order.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class RunLifecycleLedgerFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunLifecycleLedgerFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Successful_run_emits_complete_lifecycle_sequence_in_order()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var runLevelRecords = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.NodeId == null)
            .OrderBy(r => r.Sequence)
            .Select(r => r.RecordType)
            .ToListAsync();

        // The canonical happy-path sequence — pin EVERY record in order. Adding a new run-level
        // record between any two of these requires updating this assertion AND the architecture
        // doc, so the change is impossible to ship silently.
        runLevelRecords.ShouldBe(new[]
        {
            WorkflowRunRecordTypes.RunQueued,            // WorkflowService.RunManuallyAsync
            WorkflowRunRecordTypes.RunStarted,           // engine pickup
            WorkflowRunRecordTypes.ReleaseLoaded,        // workflow_version JSON deserialised
            WorkflowRunRecordTypes.VariablesSnapshotted, // first-run snapshot persist (zero variables but still emitted)
            WorkflowRunRecordTypes.ScopeResolved,        // NodeRunScope built
            WorkflowRunRecordTypes.RunCompleted,         // engine drained the graph
        });
    }

    [Fact]
    public async Task Replay_run_emits_run_replayed_record_with_parent_lineage()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Seed a team variable so the original run writes at least one snapshot row. The
        // engine's replay-detection is `WorkflowRunVariable.AnyAsync(v => v.RunId == ...)`
        // — with no snapshot rows it can never fork into the replay path. MinimalDefinition()
        // alone produces zero rows, which is why this test seeds a team-scoped variable.
        using (var setup = _fixture.BeginScope())
        {
            await setup.Resolve<IVariableService>().SetAsync(
                VariableScope.Team, teamId, teamId, "REPLAY_MARKER", VariableValueType.String,
                JsonDocument.Parse("\"v1\"").RootElement, null, userId, CancellationToken.None);
        }

        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        // Original run executes (creates the snapshot rows).
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);

        // Stage + execute a replay.
        var replayRunId = await StageReplayAsync(originalRunId, workflowId);
        await RunEngineAsync(replayRunId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // Replay run's ledger MUST contain run.replayed with parent_run_id pointing at original.
        var replayedRecord = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == replayRunId && r.RecordType == WorkflowRunRecordTypes.RunReplayed)
            .SingleAsync();

        var payload = JsonDocument.Parse(replayedRecord.PayloadJson).RootElement;
        payload.GetProperty("parent_run_id").GetGuid().ShouldBe(originalRunId,
            "run.replayed payload MUST carry the parent_run_id so the audit lineage is self-contained");
    }

    [Fact]
    public async Task Node_started_payload_carries_resolved_config_alongside_inputs()
    {
        // Closes the audit gap "node.started only saves inputs, not config". After half a
        // year, an operator must be able to answer "what model / timeout was this node
        // running with?" by reading the ledger alone. Pin that ability here.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start",  TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                // logic.if's config carries the condition expression — this is the data
                // operators want to retrieve from the ledger.
                new() { Id = "branch", TypeKey = "logic.if",
                        Config = WorkflowsTestSeed.Json("""{"condition":"true"}"""),
                        Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",    TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start",  To = "branch" },
                new() { From = "branch", To = "end", SourceHandle = "true" },
            },
        };

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var branchStarted = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.NodeId == "branch" && r.RecordType == WorkflowRunRecordTypes.NodeStarted)
            .SingleAsync();

        var payload = JsonDocument.Parse(branchStarted.PayloadJson).RootElement;
        payload.GetProperty("config").GetProperty("condition").GetString().ShouldBe("true",
            "node.started.payload_json.config MUST carry the resolved config so operators can answer 'what was this node running with' from the ledger alone");
        // inputs key still present (could be empty) — schema stays stable.
        payload.TryGetProperty("inputs", out _).ShouldBeTrue();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();
        return await mediator.Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "lifecycle-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<Guid> StageReplayAsync(Guid originalRunId, Guid workflowId)
    {
        // Mirrors the staging in WorkflowRunReplayFlowTests — clone snapshot rows + create
        // a workflow_run_request + workflow_run pointing at the original. Engine sees the
        // pre-existing snapshot and forks into replay path. Without cloning the snapshot
        // rows the engine's `WorkflowRunVariable.AnyAsync(v => v.RunId == ...)` returns
        // false and it goes through the FRESH path — run.replayed never fires.
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var original = await db.WorkflowRun.AsNoTracking()
            .Include(r => r.RunRequest)
            .SingleAsync(r => r.Id == originalRunId);

        var originalSnapshot = await db.WorkflowRunVariable.AsNoTracking()
            .Where(v => v.RunId == originalRunId)
            .ToListAsync();

        var replayRequestId = Guid.NewGuid();
        var replayId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new PersistenceEntities.WorkflowRunRequest
        {
            Id = replayRequestId,
            TeamId = original.RunRequest.TeamId,
            WorkflowId = workflowId,
            SourceType = WorkflowRunSourceTypes.Replay,
            ActorType = "user",
            ActorId = SystemUsers.SeederId,
            CausationId = original.RunRequestId,
            NormalizedPayloadJson = original.RunRequest.NormalizedPayloadJson,
            Status = MessageEnums.WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new PersistenceEntities.WorkflowRun
        {
            Id = replayId,
            WorkflowId = workflowId,
            WorkflowVersion = original.WorkflowVersion,
            TeamId = original.TeamId,
            RunRequestId = replayRequestId,
            ReleaseHashAtRun = original.ReleaseHashAtRun,
            ParentRunId = originalRunId,
            // See the matching comment in WorkflowRunReplayFlowTests.StageReplayAsync.
            // Tests bypass the dispatcher and engine.ExecuteRunAsync's entry CAS requires Enqueued.
            Status = MessageEnums.WorkflowRunStatus.Enqueued,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        foreach (var s in originalSnapshot)
        {
            db.WorkflowRunVariable.Add(new PersistenceEntities.WorkflowRunVariable
            {
                Id = Guid.NewGuid(),
                RunId = replayId,
                Scope = s.Scope,
                Name = s.Name,
                ValueType = s.ValueType,
                ValuePlain = s.ValuePlain,
                CapturedAt = now,
            });
        }

        await db.SaveChangesAsync();
        return replayId;
    }
}
