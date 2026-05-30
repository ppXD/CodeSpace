using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Engine v2 Phase 0 — the durable, re-entrant walker. The engine now rebuilds its settled
/// state from the ledger (the <c>workflow_run_node</c> view) on every entry, so a run that is
/// re-dispatched (reconciler re-enqueues an apparently-stuck run, a Hangfire retry, a
/// multi-replica race) resumes from where it stopped instead of restarting.
///
/// These tests pin the two guarantees that matter:
///   1. Idempotent re-entry — re-running a COMPLETED run re-executes NO node (no duplicate
///      side effects) and preserves the routing decisions (a skipped branch stays skipped).
///   2. Partial resume — a run with persisted progress runs only the not-yet-settled nodes.
///
/// Both FAIL on the pre-Phase-0 engine (which started the walk from the roots every time and
/// re-ran every node) and PASS on the durable walker.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DurableWalkerReentryFlowTests
{
    private readonly PostgresFixture _fixture;

    public DurableWalkerReentryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Re_dispatching_a_completed_run_re_executes_no_node_and_preserves_routing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, BranchedDefinition(condition: "true"));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // First pass — runs to completion. Branch routes "true": true_end runs, false_end skips.
        await RunEngineAsync(runId);
        var startedCountAfterFirst = await NodeStartedCountAsync(runId);
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success);

        // Simulate a re-dispatch (the reconciler resets an "abandoned" run to Enqueued, or
        // Hangfire retries the job): flip the run back to Enqueued so the engine's
        // Enqueued->Running CAS claims it again.
        await ReEnqueueAsync(runId);
        await RunEngineAsync(runId);

        // The second pass must be a pure no-op walk: rehydrate sees every node settled.
        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success,
            "re-running a settled run stays Success");
        (await NodeStartedCountAsync(runId)).ShouldBe(startedCountAfterFirst,
            "NO node may emit a second node.started — rehydrate marks them all settled and skips re-execution. " +
            "A higher count means the walker restarted from scratch and re-ran completed (possibly side-effecting) nodes.");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var nodes = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId).ToListAsync();

        nodes.Single(n => n.NodeId == "branch").Status.ShouldBe(NodeStatus.Success);
        nodes.Single(n => n.NodeId == "true_end").Status.ShouldBe(NodeStatus.Success);
        nodes.Single(n => n.NodeId == "false_end").Status.ShouldBe(NodeStatus.Skipped,
            "the persisted routing hints must rehydrate so the dead branch stays Skipped — if hints were lost, " +
            "the false edge would look live and false_end would wrongly re-run as Success");
    }

    [Fact]
    public async Task A_run_resumes_from_persisted_progress_without_re_running_completed_nodes()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition()); // start(trigger) -> end(terminal)
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Pre-record the trigger as already completed, as if a prior pass ran it and then the
        // engine crashed before reaching the terminal.
        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            var empty = (IReadOnlyDictionary<string, JsonElement>)new Dictionary<string, JsonElement>();
            await logger.NodeStartedAsync(runId, "start", iterationKey: "", empty, empty, CancellationToken.None);
            await logger.NodeCompletedAsync(runId, "start", iterationKey: "", empty, routingHints: null, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        }

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var triggerStarts = await db.WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == "start" && r.RecordType == WorkflowRunRecordTypes.NodeStarted);
        triggerStarts.ShouldBe(1, "rehydrate sees the trigger as settled — it must NOT re-execute (only the one seeded node.started exists)");

        var end = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "end");
        end.Status.ShouldBe(NodeStatus.Success, "the not-yet-run terminal executes in the resumed pass");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "reentry-" + Guid.NewGuid().ToString("N")[..6],
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

    private async Task ReEnqueueAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        // Status is stored as a string (enum HasConversion<string>); 'Enqueued' is the state the
        // engine's entry CAS claims. Mirrors what the stuck-run reconciler does to an abandoned run.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_run SET status = 'Enqueued' WHERE id = {runId}");
    }

    private async Task<WorkflowRunStatus> RunStatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status;
    }

    private async Task<int> NodeStartedCountAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeStarted);
    }

    private static WorkflowDefinition BranchedDefinition(string condition) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start",     TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "branch",    TypeKey = "logic.if",
                    Config = WorkflowsTestSeed.Json($$"""{"condition":"{{condition}}"}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "true_end",  TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "false_end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start",  To = "branch" },
            new() { From = "branch", To = "true_end",  SourceHandle = "true" },
            new() { From = "branch", To = "false_end", SourceHandle = "false" },
        },
    };
}
