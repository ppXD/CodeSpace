using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
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
/// Engine v2 Phase 3 — <c>flow.subworkflow</c>, against real Postgres + the real engine. A parent
/// node runs another workflow as a step: the parent SUSPENDS, a child run executes (parent_run_id
/// links them), and the child's completion resumes the parent — mapping the child's outputs onto the
/// node on success, or failing the node (which composes with the Phase-2 error branch) on failure.
/// Pins: the success round-trip + IO mapping; child failure → parent node fails; child failure →
/// the node's error branch; an unstartable child → a clean node failure; the recursion depth guard.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SubworkflowFlowTests
{
    private readonly PostgresFixture _fixture;

    public SubworkflowFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Parent_runs_child_and_maps_its_outputs_back()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var childId = await CreateWorkflowAsync(teamId, userId, EchoChildDefinition());
        var parentId = await CreateWorkflowAsync(teamId, userId, ParentDefinition(childId, inputValue: "hello-sub", withErrorBranch: false, childFails: false));
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentId, teamId);

        // ── Pass 1: the parent suspends on the sub-workflow node; a child run is staged. ──
        await RunEngineAsync(parentRunId);

        Guid childRunId;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the parent parks while the child runs");

            var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == parentRunId);
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.Subworkflow);

            var child = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.ParentRunId == parentRunId);
            child.Id.ToString().ShouldBe(wait.Token, "the wait's token is the child run id");
            childRunId = child.Id;
        }

        // ── Run the child (the in-memory job client recorded the dispatch but doesn't execute). ──
        await RunEngineAsync(childRunId);

        // The child's completion resumed the parent (Suspended → Pending → re-dispatched). Drive it.
        await RunEngineAsync(parentRunId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == childRunId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var parent = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId);
            parent.Status.ShouldBe(WorkflowRunStatus.Success, "the resumed parent completes once the child returns");

            (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == parentRunId && n.NodeId == "sub")).Status
                .ShouldBe(NodeStatus.Success);

            // The child echoed input x → output result; the parent's terminal forwarded the sub-node's
            // output to `final` — proving inputs flow in and outputs flow back out.
            var outputs = System.Text.Json.JsonDocument.Parse(parent.OutputsJson).RootElement;
            outputs.GetProperty("final").GetString().ShouldBe("hello-sub");
        }
    }

    [Fact]
    public async Task Child_failure_fails_the_parent_node_without_an_error_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var childId = await CreateWorkflowAsync(teamId, userId, FailingChildDefinition(Guid.NewGuid().ToString("N")));
        var parentId = await CreateWorkflowAsync(teamId, userId, ParentDefinition(childId, inputValue: "x", withErrorBranch: false, childFails: true));
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentId, teamId);

        await RunEngineAsync(parentRunId);
        var childRunId = await ChildRunIdAsync(parentRunId);
        await RunEngineAsync(childRunId);     // child fails
        await RunEngineAsync(parentRunId);    // parent resumes → sub node fails → run fails

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == childRunId)).Status.ShouldBe(WorkflowRunStatus.Failure);
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId)).Status
            .ShouldBe(WorkflowRunStatus.Failure, "a failed child with no error branch fails the parent");
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == parentRunId && n.NodeId == "sub")).Status
            .ShouldBe(NodeStatus.Failure);
    }

    [Fact]
    public async Task Child_failure_takes_the_nodes_error_branch()
    {
        // Phase 2 + Phase 3 compose: a failed child makes the sub-workflow node fail, which routes
        // down its `error` edge to a handler instead of failing the run.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var childId = await CreateWorkflowAsync(teamId, userId, FailingChildDefinition(Guid.NewGuid().ToString("N")));
        var parentId = await CreateWorkflowAsync(teamId, userId, ParentDefinition(childId, inputValue: "x", withErrorBranch: true, childFails: true));
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentId, teamId);

        await RunEngineAsync(parentRunId);
        await RunEngineAsync(await ChildRunIdAsync(parentRunId));
        await RunEngineAsync(parentRunId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "the child's failure is handled by the sub-workflow node's error branch");
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == parentRunId && n.NodeId == "sub")).Status
            .ShouldBe(NodeStatus.Failure);
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == parentRunId && n.NodeId == "caught")).Status
            .ShouldBe(NodeStatus.Success, "the error handler ran");
    }

    [Fact]
    public async Task Unknown_child_workflow_fails_the_node_cleanly()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        // Target a workflow id that doesn't exist — staging the child must fail the node, not crash the engine.
        var parentId = await CreateWorkflowAsync(teamId, userId, ParentDefinition(Guid.NewGuid(), inputValue: "x", withErrorBranch: false, childFails: false));
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentId, teamId);

        await RunEngineAsync(parentRunId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId)).Status.ShouldBe(WorkflowRunStatus.Failure);

        // The specific reason lives on the failed node; the run carries the generic halt message.
        var subNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == parentRunId && n.NodeId == "sub");
        subNode.Status.ShouldBe(NodeStatus.Failure);
        subNode.Error.ShouldNotBeNull();
        subNode.Error!.ShouldContain("not found");

        (await db.WorkflowRunWait.AsNoTracking().AnyAsync(w => w.RunId == parentRunId))
            .ShouldBeFalse("a node that can't start its child never parks");
    }

    [Fact]
    public async Task Nesting_deeper_than_the_cap_is_refused()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, EchoChildDefinition());

        // Seed a parent_run_id chain MaxDepth deep so the deepest run already has MaxDepth-1 ancestors.
        var chain = new List<Guid>();
        for (var i = 0; i < SubworkflowService.MaxDepth; i++)
            chain.Add(await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId));

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            for (var i = 1; i < chain.Count; i++)
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_run SET parent_run_id = {chain[i - 1]} WHERE id = {chain[i]}");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var svc = scope.Resolve<ISubworkflowService>();
            var deepestId = chain[^1];
            var deepest = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == deepestId);

            await Should.ThrowAsync<SubworkflowStartException>(
                async () => await svc.StageChildRunAsync(deepest, workflowId, null, "{}", CancellationToken.None));
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> ChildRunIdAsync(Guid parentRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.ParentRunId == parentRunId)).Id;
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "sub-" + Guid.NewGuid().ToString("N")[..6],
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

    // Child: echoes the payload's `x` to output `result`.
    private static WorkflowDefinition EchoChildDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{"result":"{{trigger.x}}"}""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
    };

    // Child: always fails (FlakyTestNode with a huge failTimes).
    private static WorkflowDefinition FailingChildDefinition(string key) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "boom", TypeKey = FlakyTestNode.Key,
                    Config = WorkflowsTestSeed.Json($$"""{"key":"{{key}}","failTimes":99}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "boom" }, new() { From = "boom", To = "end" } },
    };

    // Parent: manual → sub(child) → terminal(final = sub.result). Optionally sub =(error)=> caught.
    private static WorkflowDefinition ParentDefinition(Guid childWorkflowId, string inputValue, bool withErrorBranch, bool childFails) => new()
    {
        SchemaVersion = 1,
        Nodes = BuildParentNodes(childWorkflowId, inputValue, withErrorBranch, childFails),
        Edges = BuildParentEdges(withErrorBranch),
    };

    private static List<NodeDefinition> BuildParentNodes(Guid childWorkflowId, string inputValue, bool withErrorBranch, bool childFails)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sub", TypeKey = "flow.subworkflow",
                    Config = WorkflowsTestSeed.Json($$"""{"workflowId":"{{childWorkflowId}}"}"""),
                    Inputs = WorkflowsTestSeed.Json($$$"""{"inputs":{"x":"{{{inputValue}}}"}}""") },
            // On the success path the terminal forwards the child's `result`; a failing child never
            // reaches it, so guard the ref out to keep the definition valid either way.
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = childFails ? WorkflowsTestSeed.EmptyJson() : WorkflowsTestSeed.Json("""{"final":"{{nodes.sub.outputs.result}}"}""") },
        };
        if (withErrorBranch)
            nodes.Add(new() { Id = "caught", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() });
        return nodes;
    }

    private static List<EdgeDefinition> BuildParentEdges(bool withErrorBranch)
    {
        var edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sub" },
            new() { From = "sub", To = "end" },
        };
        if (withErrorBranch)
            edges.Add(new() { From = "sub", To = "caught", SourceHandle = WorkflowHandles.Error });
        return edges;
    }
}
