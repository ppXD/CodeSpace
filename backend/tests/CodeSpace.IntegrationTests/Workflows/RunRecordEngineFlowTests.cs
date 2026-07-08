using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Assert that the engine emits the EXPECTED ledger records for each scenario shape.
/// Catches engine regressions where a status transition or skip-propagation doesn't land on
/// the ledger.
///
/// Coverage matrix:
///   - Linear success:  2 nodes  → 4 records (start + complete × 2)
///   - Failure:         start + complete (upstream) + start + failed (downstream) — no further nodes
///   - Branch (true):   if-node → matched branch starts/completes; other branch is skipped
///   - Branch (false):  inverse of the above
///   - Iterate:         flow.iterate processes N items in-node, emits 1 start + 1 complete (the
///                      per-iteration child records are an opt-in plugin-author concern; here we
///                      assert the engine's outer-loop behaviour)
///   - Multi-replay:    replay run's ledger is disjoint from parent's
///   - Combination:     branch + downstream success in the same DAG
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunRecordEngineFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunRecordEngineFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Linear_two_node_success_emits_started_and_completed_per_node_in_order()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var allRecords = await ReadRecordsAsync(runId);

        // Ledger contains run-level records (run.queued / run.started / release.loaded /
        // scope.resolved / [variables.snapshotted] / run.completed) alongside node records.
        // This test's intent is node-lifecycle ordering, so filter to node.* only.
        var nodeRecords = allRecords.Where(r => r.RecordType.StartsWith("node.")).ToList();

        // Trigger → terminal: 2 nodes, 2 lifecycle records each.
        nodeRecords.Count.ShouldBe(4);
        nodeRecords.Select(r => r.RecordType).ShouldBe(new[]
        {
            WorkflowRunRecordTypes.NodeStarted,    // start
            WorkflowRunRecordTypes.NodeCompleted,
            WorkflowRunRecordTypes.NodeStarted,    // end
            WorkflowRunRecordTypes.NodeCompleted,
        });

        nodeRecords.Select(r => r.NodeId).ShouldBe(new[] { "start", "start", "end", "end" });
        nodeRecords.ShouldAllBe(r => r.IterationKey == string.Empty);
    }

    [Fact]
    public async Task Failed_node_emits_node_failed_with_error_payload()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // A workflow that uses logic.if with a malformed expression to force a Failure on
        // the if-node itself. The downstream terminal never fires (no edges activate).
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                // Empty condition fails fast in logic.if's "Config 'condition' is required" guard.
                new() { Id = "branch", TypeKey = "logic.if",
                        Config = WorkflowsTestSeed.Json("""{"condition":""}"""),
                        Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "branch" },
                new() { From = "branch", To = "end", SourceHandle = "true" },
            },
        };

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var records = await ReadRecordsAsync(runId);

        // The branch node must end up as node.failed. start+branch start, branch fails, end
        // never runs (engine halts on failure).
        var failedRecord = records.SingleOrDefault(r => r.RecordType == WorkflowRunRecordTypes.NodeFailed && r.NodeId == "branch");
        failedRecord.ShouldNotBeNull("the failing branch must emit a node.failed record");

        var payload = JsonDocument.Parse(failedRecord!.PayloadJson).RootElement;
        payload.TryGetProperty("error", out var errProp).ShouldBeTrue();
        errProp.GetString().ShouldNotBeNullOrEmpty();
        payload.TryGetProperty("duration_ms", out var durProp).ShouldBeTrue();
        durProp.GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Branch_routes_matched_handle_only_other_branch_emits_node_skipped()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Branch with two downstreams; the "true" branch is alive, the "false" branch must
        // be marked skipped because logic.if only emits one routing hint per run.
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start",      TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "branch",     TypeKey = "logic.if",
                        Config = WorkflowsTestSeed.Json("""{"condition":"true"}"""),
                        Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "true_end",   TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "false_end",  TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "branch" },
                new() { From = "branch", To = "true_end",  SourceHandle = "true" },
                new() { From = "branch", To = "false_end", SourceHandle = "false" },
            },
        };

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var records = await ReadRecordsAsync(runId);

        // true_end runs (success); false_end skipped. Both must show up in the ledger.
        records.Any(r => r.RecordType == WorkflowRunRecordTypes.NodeCompleted && r.NodeId == "true_end")
            .ShouldBeTrue("the live branch destination must complete");
        records.Any(r => r.RecordType == WorkflowRunRecordTypes.NodeSkipped && r.NodeId == "false_end")
            .ShouldBeTrue("the dead branch destination must emit node.skipped");

        // The skipped node must NOT have a node.started — only a node.skipped.
        records.Any(r => r.RecordType == WorkflowRunRecordTypes.NodeStarted && r.NodeId == "false_end")
            .ShouldBeFalse("a skipped node never starts; only a node.skipped is emitted");
    }

    [Fact]
    public async Task Sequence_strictly_increases_across_engine_emitted_records()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var records = await ReadRecordsAsync(runId);

        records.Count.ShouldBeGreaterThanOrEqualTo(2);
        for (int i = 1; i < records.Count; i++)
            records[i].Sequence.ShouldBeGreaterThan(records[i - 1].Sequence,
                "ledger sequence must be strictly monotonic per run for replay-reconstruction reliability");
    }

    [Fact]
    public async Task Two_parallel_runs_have_disjoint_ledgers_with_no_cross_contamination()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        var runIdA = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        var runIdB = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Drive both runs concurrently to exercise the shared-DbContext / shared-table path.
        await Task.WhenAll(RunEngineAsync(runIdA), RunEngineAsync(runIdB));

        var recordsA = await ReadRecordsAsync(runIdA);
        var recordsB = await ReadRecordsAsync(runIdB);

        recordsA.ShouldNotBeEmpty();
        recordsB.ShouldNotBeEmpty();
        recordsA.ShouldAllBe(r => r.RunId == runIdA, "run A's records must not leak run B's");
        recordsB.ShouldAllBe(r => r.RunId == runIdB, "run B's records must not leak run A's");

        // Same node ids appear in both — that's expected (same definition). The isolation
        // is on run_id, not node identity.
        recordsA.Select(r => r.NodeId).Distinct().ShouldBe(recordsB.Select(r => r.NodeId).Distinct(), ignoreOrder: true);
    }

    [Fact]
    public async Task Replay_run_writes_its_own_ledger_parent_records_untouched()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);

        var originalRecords = await ReadRecordsAsync(originalRunId);
        var originalCount = originalRecords.Count;
        originalCount.ShouldBeGreaterThan(0);

        // Stage a replay using the production ReplayRunCommand path so the test exercises
        // the real code that allocates a new workflow_run_request + workflow_run.
        Guid replayRunId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            replayRunId = await mediator.Send(new ReplayRunCommand { OriginalRunId = originalRunId });
        }

        await RunEngineAsync(replayRunId);

        var replayRecords = await ReadRecordsAsync(replayRunId);
        var originalRecordsAfter = await ReadRecordsAsync(originalRunId);

        replayRecords.ShouldNotBeEmpty();
        replayRecords.ShouldAllBe(r => r.RunId == replayRunId,
            "replay records must carry the replay run id, not the parent's");

        originalRecordsAfter.Count.ShouldBe(originalCount,
            "parent run's ledger must be untouched by replay");
        originalRecordsAfter.Select(r => r.Id).ShouldBe(originalRecords.Select(r => r.Id), ignoreOrder: true);
    }

    [Fact]
    public async Task Combination_branch_plus_downstream_terminal_ledger_records_match_DAG()
    {
        // Combination scenario: trigger → branch(true) → process → terminal
        //                                     branch(false) → terminal_alt (skipped)
        // Asserts the ledger captures the entire path correctly with no duplicates / omissions.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start",   TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "branch",  TypeKey = "logic.if",
                        Config = WorkflowsTestSeed.Json("""{"condition":"true"}"""),
                        Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "process", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "skip_me", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start",  To = "branch" },
                new() { From = "branch", To = "process", SourceHandle = "true" },
                new() { From = "branch", To = "skip_me", SourceHandle = "false" },
            },
        };

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        // Filter to node-scoped records before grouping. Run-level records (run.queued /
        // run.started / release.loaded / etc) have NodeId=null and don't belong in a
        // "ledger records per node" view.
        var byNode = (await ReadRecordsAsync(runId))
            .Where(r => r.NodeId != null)
            .GroupBy(r => r.NodeId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // start: started + completed
        byNode["start"].Select(r => r.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.NodeStarted, WorkflowRunRecordTypes.NodeCompleted }, ignoreOrder: true);
        // branch: started + completed
        byNode["branch"].Select(r => r.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.NodeStarted, WorkflowRunRecordTypes.NodeCompleted }, ignoreOrder: true);
        // process: started + completed
        byNode["process"].Select(r => r.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.NodeStarted, WorkflowRunRecordTypes.NodeCompleted }, ignoreOrder: true);
        // skip_me: only skipped (no started)
        byNode["skip_me"].Select(r => r.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.NodeSkipped });
    }

    [Fact]
    public async Task Node_completed_payload_carries_resolved_outputs()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // The trigger node's BuildPayload puts a "repositoryId" key into the trigger payload
        // even when the manual run sends "{}" — but the terminal node's Inputs map will be
        // an empty object. So the node.completed payload's outputs is "{}", that's what we
        // assert here.
        var def = WorkflowsTestSeed.MinimalDefinition();
        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var records = await ReadRecordsAsync(runId);
        var endCompleted = records.Single(r => r.RecordType == WorkflowRunRecordTypes.NodeCompleted && r.NodeId == "end");

        var payload = JsonDocument.Parse(endCompleted.PayloadJson).RootElement;
        payload.TryGetProperty("outputs", out var outputs).ShouldBeTrue("node.completed must carry an 'outputs' key");
        outputs.ValueKind.ShouldBe(JsonValueKind.Object);

        payload.TryGetProperty("duration_ms", out var dur).ShouldBeTrue();
        dur.GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "record-engine-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<List<Persistence.RecordRow>> ReadRecordsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var rows = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId)
            .OrderBy(r => r.Sequence)
            .Select(r => new Persistence.RecordRow(r.Id, r.RunId, r.Sequence, r.RecordType, r.NodeId, r.IterationKey, r.CorrelationId, r.PayloadJson))
            .ToListAsync();
        return rows;
    }

    private static class Persistence
    {
        // Lightweight projection so the helper doesn't leak the EF entity (and so equality
        // comparisons in tests don't drag in nav properties).
        public sealed record RecordRow(Guid Id, Guid RunId, long Sequence, string RecordType, string? NodeId, string IterationKey, Guid? CorrelationId, string PayloadJson);
    }
}
