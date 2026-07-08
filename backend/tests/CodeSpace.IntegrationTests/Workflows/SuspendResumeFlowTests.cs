using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Engine v2 Phase 1 — end-to-end suspend / resume via the <c>flow.sleep</c> node, against real
/// Postgres + the real engine. Proves the whole mechanism: a node returns Suspend → the run
/// parks (Suspended) with a wait row + node.suspended record and downstream does NOT run →
/// a resume signal resolves the wait, flips Suspended→Pending, and re-dispatches → the durable
/// walker rehydrates, re-runs the node with its ResumePayload, and the run completes.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SuspendResumeFlowTests
{
    private readonly PostgresFixture _fixture;

    public SuspendResumeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Sleep_node_suspends_the_run_then_resumes_to_success()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SleepDefinition(seconds: 60));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // ── First pass: the sleep node parks the run ───────────────────────────
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
            run.Status.ShouldBe(WorkflowRunStatus.Suspended, "the run parks on the sleep node — it does NOT complete");
            run.CompletedAt.ShouldBeNull("a suspended run is not terminal");

            var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
            wait.NodeId.ShouldBe("sleep");
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.Timer);
            wait.Status.ShouldBe(WorkflowWaitStatuses.Pending);
            wait.WakeAt.ShouldNotBeNull("a Timer wait records when the scheduled resume fires");

            var sleepNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "sleep");
            sleepNode.Status.ShouldBe(NodeStatus.Suspended, "node.suspended projects to NodeStatus.Suspended on the view");

            var terminalRan = await db.WorkflowRunNode.AsNoTracking().AnyAsync(n => n.RunId == runId && n.NodeId == "end");
            terminalRan.ShouldBeFalse("downstream of a suspended node must NOT run until the run resumes");
        }

        // ── Resume signal (what the scheduled timer job invokes) ────────────────
        bool resumed;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var waitId = (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId)).Id;
            resumed = await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None);
        }
        resumed.ShouldBeTrue("resuming a Suspended run succeeds");

        // The resume re-dispatches (Suspended→Pending→Enqueued); the in-memory job client records
        // but doesn't execute, so drive the engine for the resume pass.
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
            run.Status.ShouldBe(WorkflowRunStatus.Success,
                "the resumed run re-runs the sleep node (now with a ResumePayload) to Success and reaches the terminal");

            (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "sleep")).Status
                .ShouldBe(NodeStatus.Success, "on resume the sleep node completes");
            (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "end")).Status
                .ShouldBe(NodeStatus.Success, "the previously-blocked terminal now runs");

            (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId)).Status
                .ShouldBe(WorkflowWaitStatuses.Resolved, "the wait is resolved by the resume signal");
        }

        // ── Idempotency: resuming a no-longer-suspended run is a no-op ──────────
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var waitId = (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId)).Id;
            (await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None))
                .ShouldBeFalse("a second resume of an already-resumed/terminal run does nothing");
        }
    }

    [Fact]
    public async Task Sleep_node_with_non_positive_delay_fails_the_run()
    {
        // A misconfigured sleep (seconds <= 0) returns Failure on the first pass — it must NOT
        // suspend with a bogus wait. The run fails like any other node failure.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SleepDefinition(seconds: 0));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Failure);
        (await db.WorkflowRunWait.AsNoTracking().AnyAsync(w => w.RunId == runId)).ShouldBeFalse("a failed sleep writes no wait row");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "sleep-" + Guid.NewGuid().ToString("N")[..6],
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

    private static WorkflowDefinition SleepDefinition(int seconds) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sleep", TypeKey = "flow.sleep",
                    Config = WorkflowsTestSeed.Json($$"""{"seconds":{{seconds}}}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sleep" },
            new() { From = "sleep", To = "end" },
        },
    };
}
