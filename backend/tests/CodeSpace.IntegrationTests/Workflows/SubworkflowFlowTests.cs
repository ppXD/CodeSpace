using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Workflows;
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
    public async Task Rerun_from_a_subworkflow_node_restages_a_fresh_child_and_completes()
    {
        // D2: the flow.subworkflow node is now an admitted from-node rerun ROOT. A rerun re-executes the node on the
        // fork, staging a FRESH child run (parent_run_id = the fork) — unique by construction like the agent.run
        // re-stage — that runs to completion; the ORIGINAL run + its child are untouched (no cross-run mutation).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var childId = await CreateWorkflowAsync(teamId, userId, EchoChildDefinition());
        var parentId = await CreateWorkflowAsync(teamId, userId, ParentDefinition(childId, inputValue: "hello-sub", withErrorBranch: false, childFails: false));
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentId, teamId);

        // Original: parent → child → Success.
        await RunEngineAsync(originalRunId);
        var originalChildId = await ChildRunIdAsync(originalRunId);
        await RunEngineAsync(originalChildId);
        await RunEngineAsync(originalRunId);
        await AssertStatusAsync(originalRunId, WorkflowRunStatus.Success);

        // Rerun FROM the "sub" node — before D2 this was RefuseSuspendable; now it re-stages.
        var forkRunId = await RerunFromNodeAsync(originalRunId, "sub", teamId, userId);
        forkRunId.ShouldNotBe(originalRunId, "the rerun mints a fresh forked parent run");

        // The fork re-executes "sub" → a FRESH child under the fork, distinct from the original's child.
        await RunEngineAsync(forkRunId);
        var forkChildId = await ChildRunIdAsync(forkRunId);
        forkChildId.ShouldNotBe(originalChildId, "the rerun stages a FRESH child under the fork — it never reuses the original's child");

        await RunEngineAsync(forkChildId);
        await RunEngineAsync(forkRunId);

        await AssertStatusAsync(forkRunId, WorkflowRunStatus.Success, "the re-run parent completes once its fresh child returns");
        await AssertStatusAsync(originalChildId, WorkflowRunStatus.Success);

        // The original's SUBWORKFLOW child is untouched — exactly one child-workflow run under the original (the fork
        // also has ParentRunId == original in the rerun lineage, so filter to the child-workflow source to isolate it).
        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking()
            .CountAsync(r => r.ParentRunId == originalRunId && r.SourceType == WorkflowRunSourceTypes.ChildWorkflow))
            .ShouldBe(1, "the original run still has exactly its one subworkflow child — the rerun didn't mutate the original lineage");
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

    [Fact]
    public async Task Successive_reruns_do_not_inflate_the_nesting_depth_guard()
    {
        // D2 regression: a from-node RERUN fork links via parent_run_id as rerun LINEAGE, not subworkflow nesting.
        // A chain of (MaxDepth + 2) such forks — deeper than the cap — must NOT trip the recursion guard, else N
        // successive reruns of a subworkflow node would spuriously fail on ZERO real nesting. (Contrast
        // Nesting_deeper_than_the_cap_is_refused, which DOES trip on a real parent-run nesting chain.)
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, EchoChildDefinition());

        var chain = new List<Guid>();
        for (var i = 0; i < SubworkflowService.MaxDepth + 2; i++)
            chain.Add(await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId));

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            // Link the chain + mark every non-root run a RERUN fork (rerun lineage, not nesting).
            for (var i = 1; i < chain.Count; i++)
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_run SET parent_run_id = {chain[i - 1]}, source_type = {WorkflowRunSourceTypes.Rerun} WHERE id = {chain[i]}");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var svc = scope.Resolve<ISubworkflowService>();
            var deepestId = chain[^1];
            var deepest = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == deepestId);

            // No throw: every ancestor hop is rerun lineage, so the nesting depth stays 0 and the child stages fine.
            var childRunId = await svc.StageChildRunAsync(deepest, workflowId, null, "{}", CancellationToken.None);
            childRunId.ShouldNotBe(Guid.Empty, "a deep rerun-lineage chain stages a child fine — rerun hops are NOT nesting levels");
        }
    }

    [Fact]
    public async Task Parent_resumes_after_the_child_workflows_internal_approval()
    {
        // The composition the inline-embed UX is built for: a sub-workflow that itself contains an
        // approval. Parent suspends on the sub-workflow → child suspends on ITS approval → approving
        // the CHILD completes it → the engine resumes the PARENT, mapping the decision back out.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var childId = await CreateWorkflowAsync(teamId, userId, ApprovalChildDefinition());
        var parentId = await CreateWorkflowAsync(teamId, userId, ApprovalParentDefinition(childId));
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentId, teamId);

        await RunEngineAsync(parentRunId);                 // parent suspends on the sub-workflow
        var childRunId = await ChildRunIdAsync(parentRunId);
        await RunEngineAsync(childRunId);                  // child runs → suspends on its own approval

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == childRunId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the child parks on its own approval");
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the parent stays parked while the child awaits approval");
            (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == childRunId)).WaitKind
                .ShouldBe(WorkflowWaitKinds.Approval);
        }

        // Approve the CHILD (what the inline panel does — it posts to the child run).
        (await ApproveAsync(childRunId, teamId, userId)).ShouldBeTrue();

        await RunEngineAsync(childRunId);                  // child resumes → completes
        await RunEngineAsync(parentRunId);                 // completion hook resumed the parent → it finishes

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == childRunId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var parent = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId);
            parent.Status.ShouldBe(WorkflowRunStatus.Success, "approving the child resumes + completes the parent");
            // The child's approval decision propagated all the way out to the parent's outputs.
            System.Text.Json.JsonDocument.Parse(parent.OutputsJson).RootElement.GetProperty("decision").GetBoolean()
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Run_detail_links_the_subworkflow_node_to_its_child_run()
    {
        // The data the node-list drill-down UI needs: the sub-workflow STEP carries its child run
        // id, so the run-detail can embed / link the child inline — both while the step is suspended
        // and after it finished (the wait row persists post-resolution). Other nodes carry none.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var childId = await CreateWorkflowAsync(teamId, userId, EchoChildDefinition());
        var parentId = await CreateWorkflowAsync(teamId, userId, ParentDefinition(childId, inputValue: "x", withErrorBranch: false, childFails: false));
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentId, teamId);

        await RunEngineAsync(parentRunId);                 // parent suspends on the sub-workflow
        var childRunId = await ChildRunIdAsync(parentRunId);

        // While suspended: the sub node already points at its child; the trigger node points at nothing.
        var suspendedDetail = await GetRunDetailAsync(parentRunId, teamId, userId);
        suspendedDetail.Nodes.Single(n => n.NodeId == "sub").ChildRunId
            .ShouldBe(childRunId.ToString(), "the suspended sub-workflow step links its child run");
        suspendedDetail.Nodes.Single(n => n.NodeId == "start").ChildRunId
            .ShouldBeNull("a non-subworkflow node has no child run");

        await RunEngineAsync(childRunId);                  // child completes
        await RunEngineAsync(parentRunId);                 // parent resumes → finishes

        // After completion: the link still holds (the resolved wait row is not deleted), so the
        // finished step stays drillable.
        var finishedDetail = await GetRunDetailAsync(parentRunId, teamId, userId);
        finishedDetail.Status.ShouldBe(WorkflowRunStatus.Success);
        finishedDetail.Nodes.Single(n => n.NodeId == "sub").ChildRunId
            .ShouldBe(childRunId.ToString(), "a finished sub-workflow step keeps its child-run link");
    }

    [Fact]
    public async Task Two_parallel_subworkflow_children_each_resume_their_own_parent_node()
    {
        // The subworkflow analog of the parallel-agent fix: two flow.subworkflow nodes run children in ONE
        // wave; each parent node MUST resume with ITS OWN child's outputs (not the first completer's), both
        // children are staged + dispatched, and the parent advances only after the LAST child finishes.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var childId = await CreateWorkflowAsync(teamId, userId, EchoChildDefinition());
        var parentId = await CreateWorkflowAsync(teamId, userId, TwoSubsParentDefinition(childId));
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentId, teamId);

        // ── Pass 1: both sub nodes park in one wave → two Subworkflow waits, two children staged. ──
        await RunEngineAsync(parentRunId);

        Guid child1, child2;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId)).Status.ShouldBe(WorkflowRunStatus.Suspended);

            var waits = await db.WorkflowRunWait.AsNoTracking()
                .Where(w => w.RunId == parentRunId && w.Status == WorkflowWaitStatuses.Pending).ToListAsync();
            waits.Count.ShouldBe(2, "both sub-workflow nodes park on their own child wait");
            child1 = Guid.Parse(waits.Single(w => w.NodeId == "sub1").Token);
            child2 = Guid.Parse(waits.Single(w => w.NodeId == "sub2").Token);

            (await db.WorkflowRun.AsNoTracking().CountAsync(r => r.ParentRunId == parentRunId))
                .ShouldBe(2, "both children are staged + dispatched — not just the first");
        }

        // ── Child 1 completes first → resolves ONLY sub1's wait; the parent stays parked for sub2. ──
        await RunEngineAsync(child1);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "one of two parallel children finishing does NOT advance the parent");
            (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == parentRunId && w.NodeId == "sub2")).Status
                .ShouldBe(WorkflowWaitStatuses.Pending, "sub2's wait is untouched by child1's completion (the corruption resolved it too)");
        }

        // ── Child 2 completes → the last wait → the parent advances; each node maps its OWN child. ──
        await RunEngineAsync(child2);
        await RunEngineAsync(parentRunId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == parentRunId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var sub1 = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == parentRunId && n.NodeId == "sub1");
            var sub2 = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == parentRunId && n.NodeId == "sub2");
            System.Text.Json.JsonDocument.Parse(sub1.OutputsJson!).RootElement.GetProperty("result").GetString()
                .ShouldBe("CHILD-1", "sub1 resumes with child1's output");
            System.Text.Json.JsonDocument.Parse(sub2.OutputsJson!).RootElement.GetProperty("result").GetString()
                .ShouldBe("CHILD-2", "sub2 resumes with child2's output — NOT the first completer's (the bug)");
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    // Parent: manual → { sub1(child, x=CHILD-1), sub2(child, x=CHILD-2) } in parallel → join terminal.
    private static WorkflowDefinition TwoSubsParentDefinition(Guid childWorkflowId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sub1", TypeKey = "flow.subworkflow",
                    Config = WorkflowsTestSeed.Json($$"""{"workflowId":"{{childWorkflowId}}"}"""),
                    Inputs = WorkflowsTestSeed.Json("""{"inputs":{"x":"CHILD-1"}}""") },
            new() { Id = "sub2", TypeKey = "flow.subworkflow",
                    Config = WorkflowsTestSeed.Json($$"""{"workflowId":"{{childWorkflowId}}"}"""),
                    Inputs = WorkflowsTestSeed.Json("""{"inputs":{"x":"CHILD-2"}}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{"a":"{{nodes.sub1.outputs.result}}","b":"{{nodes.sub2.outputs.result}}"}""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sub1" },
            new() { From = "start", To = "sub2" },
            new() { From = "sub1", To = "end" },
            new() { From = "sub2", To = "end" },
        },
    };

    private async Task<WorkflowRunDetail> GetRunDetailAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var detail = await scope.Resolve<IMediator>().Send(new GetWorkflowRunQuery { RunId = runId });
        detail.ShouldNotBeNull();
        return detail!;
    }

    private async Task<bool> ApproveAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = true });
    }

    // Child: manual → wait_approval → terminal that echoes the approval decision as `ok`.
    private static WorkflowDefinition ApprovalChildDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "approval", TypeKey = "flow.wait_approval",
                    Config = WorkflowsTestSeed.Json("""{"prompt":"Approve the review?"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{"ok":"{{nodes.approval.outputs.approved}}"}""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "approval" }, new() { From = "approval", To = "end" } },
    };

    // Parent: manual → sub(child) → terminal that surfaces the child's `ok` as `decision`.
    private static WorkflowDefinition ApprovalParentDefinition(Guid childWorkflowId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sub", TypeKey = "flow.subworkflow",
                    Config = WorkflowsTestSeed.Json($$"""{"workflowId":"{{childWorkflowId}}"}"""),
                    Inputs = WorkflowsTestSeed.Json("""{"inputs":{}}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{"decision":"{{nodes.sub.outputs.ok}}"}""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "sub" }, new() { From = "sub", To = "end" } },
    };

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

    private async Task<Guid> RerunFromNodeAsync(Guid originalRunId, string fromNodeId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().RerunFromNodeAsync(originalRunId, fromNodeId, teamId, userId, CancellationToken.None);
    }

    private async Task AssertStatusAsync(Guid runId, WorkflowRunStatus expected, string? because = null)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(expected, because ?? $"run {runId}; error={run.Error}");
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
