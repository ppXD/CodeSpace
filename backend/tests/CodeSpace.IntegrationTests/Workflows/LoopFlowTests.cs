using System.Text.Json;
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
/// Engine v2 Phase 3 — <c>flow.loop</c>, against real Postgres + the real engine. A loop container
/// owns a body subgraph (nodes whose ParentId is the loop, rooted at a flow.loop_start) and re-runs
/// it once per iteration until a termination condition is met or the iteration cap is hit; loop
/// variables thread state across passes. Pins: condition-met exit + variable threading + the seen
/// sequence; the max-iterations cap; a body failure (no error edge) failing the loop under the default
/// terminate policy; continue-on-error keeping the loop alive (all-fail and partial-fail-then-terminate);
/// an error edge INSIDE the body being handled; and a SUSPENDING body node (approval) parking the run
/// durably and resuming once per iteration — including a loop variable threaded correctly across the
/// suspend. Each pass persists its body nodes under iteration key "&lt;loopId&gt;#&lt;i&gt;".
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class LoopFlowTests
{
    private readonly PostgresFixture _fixture;

    public LoopFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Loop_runs_until_the_condition_and_threads_a_variable()
    {
        var key = "probe-" + Guid.NewGuid().ToString("N");
        LoopProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        // acc starts "start"; each pass appends ":<index>". Terminate when loop.index eq "2".
        var workflowId = await CreateWorkflowAsync(teamId, userId, ConditionLoopDefinition(key, terminateAtIndex: "2", maxIterations: 10));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var loop = await LoopNodeAsync(db, runId);
        loop.Status.ShouldBe(NodeStatus.Success);

        var outputs = JsonDocument.Parse(loop.OutputsJson).RootElement;
        outputs.GetProperty("iterations").GetInt32().ShouldBe(3, "i=0,1,2 — the check at the end of pass 2 sees index==2");
        outputs.GetProperty("terminationReason").GetString().ShouldBe("condition");
        outputs.GetProperty("acc").GetString().ShouldBe("start:0:1:2", "the update ref threaded acc across every pass");

        // The body saw the PREVIOUS pass's accumulated value each iteration — proves real threading.
        LoopProbeNode.SeenFor(key).ShouldBe(new[] { "start", "start:0", "start:0:1" });

        // Each pass persisted the body probe under its own iteration key.
        var probeKeys = await db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == "probe").Select(n => n.IterationKey).ToListAsync();
        probeKeys.OrderBy(k => k).ShouldBe(new[] { "loop#0", "loop#1", "loop#2" });
    }

    [Fact]
    public async Task Loop_stops_at_the_max_iterations_cap()
    {
        var key = "probe-" + Guid.NewGuid().ToString("N");
        LoopProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        // Condition never met (index never reaches 99); the cap of 3 stops it.
        var workflowId = await CreateWorkflowAsync(teamId, userId, ConditionLoopDefinition(key, terminateAtIndex: "99", maxIterations: 3));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var outputs = JsonDocument.Parse((await LoopNodeAsync(db, runId)).OutputsJson).RootElement;
        outputs.GetProperty("iterations").GetInt32().ShouldBe(3);
        outputs.GetProperty("terminationReason").GetString().ShouldBe("maxIterations");
        LoopProbeNode.SeenFor(key).Count.ShouldBe(3, "the cap bounds the body to exactly maxIterations passes");
    }

    [Fact]
    public async Task Body_failure_with_no_error_edge_fails_the_loop_and_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FailingBodyDefinition(Guid.NewGuid().ToString("N"), withErrorBranch: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Failure, "an unhandled body failure fails the loop, which fails the run");
        (await LoopNodeAsync(db, runId)).Status.ShouldBe(NodeStatus.Failure);
    }

    [Fact]
    public async Task Error_edge_inside_the_body_is_handled_and_the_loop_succeeds()
    {
        // Phase 2 + Phase 3 compose: a failing body node routes down its `error` edge to a handler
        // INSIDE the loop body, so the iteration succeeds and the loop completes normally.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FailingBodyDefinition(Guid.NewGuid().ToString("N"), withErrorBranch: true));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await LoopNodeAsync(db, runId)).Status.ShouldBe(NodeStatus.Success);
        // The in-body error handler ran on the first iteration.
        (await db.WorkflowRunNode.AsNoTracking().AnyAsync(n => n.RunId == runId && n.NodeId == "caught" && n.Status == NodeStatus.Success))
            .ShouldBeTrue("the body's error-branch handler ran");
    }

    [Fact]
    public async Task Continue_on_error_keeps_the_loop_alive_when_every_pass_fails()
    {
        // errorHandling:"continue" — boom fails every pass (failTimes huge), but instead of sinking the
        // loop, each failed pass is skipped and the loop runs the full cap, succeeding overall.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId,
            FailingBodyDefinition(Guid.NewGuid().ToString("N"), withErrorBranch: false, errorHandling: "continue", failTimes: 99, maxIterations: 3, terminateAtIndex: "99"));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "continue-on-error: an unhandled body failure no longer sinks the loop");

        var loop = await LoopNodeAsync(db, runId);
        loop.Status.ShouldBe(NodeStatus.Success);

        var outputs = JsonDocument.Parse(loop.OutputsJson).RootElement;
        outputs.GetProperty("iterations").GetInt32().ShouldBe(3, "ran the full cap — no pass ever succeeded to meet a condition");
        outputs.GetProperty("failedIterations").GetInt32().ShouldBe(3, "every pass failed and was skipped");
        outputs.GetProperty("terminationReason").GetString().ShouldBe("maxIterations");

        // Each pass still recorded its body failure under its own iteration key — observability is intact.
        var boomFailures = await db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == "boom" && n.Status == NodeStatus.Failure)
            .Select(n => n.IterationKey).ToListAsync();
        boomFailures.OrderBy(k => k).ShouldBe(new[] { "loop#0", "loop#1", "loop#2" });
    }

    [Fact]
    public async Task Continue_on_error_skips_a_failed_pass_then_a_later_passing_one_can_terminate()
    {
        // boom fails the FIRST pass then succeeds; terminate when loop.index == 1. Proves a failed pass
        // does NOT get to satisfy termination (it's skipped), but the next, passing one does.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId,
            FailingBodyDefinition(Guid.NewGuid().ToString("N"), withErrorBranch: false, errorHandling: "continue", failTimes: 1, maxIterations: 5, terminateAtIndex: "1"));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var outputs = JsonDocument.Parse((await LoopNodeAsync(db, runId)).OutputsJson).RootElement;
        outputs.GetProperty("iterations").GetInt32().ShouldBe(2, "pass#0 failed (skipped), pass#1 succeeded and met index==1");
        outputs.GetProperty("failedIterations").GetInt32().ShouldBe(1);
        outputs.GetProperty("terminationReason").GetString().ShouldBe("condition");
    }

    [Fact]
    public async Task Approval_in_a_loop_body_parks_then_resumes_once_per_iteration()
    {
        // The case the user hits: an approval INSIDE a loop. Each pass parks the run on its OWN
        // iteration-keyed approval wait; approving re-enters the loop AT that pass and carries on. A
        // 3-iteration loop ⇒ three separate approvals, then the run completes — durable across each park.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ApprovalLoopDefinition(maxIterations: 3));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        for (var pass = 0; pass < 3; pass++)
        {
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, $"pass {pass}: parked on this iteration's approval");
                var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
                wait.WaitKind.ShouldBe(WorkflowWaitKinds.Approval);
                wait.IterationKey.ShouldBe($"loop#{pass}", "the wait is keyed to the iteration that suspended");
            }

            (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
        }

        await RunEngineAsync(runId);

        using var final = _fixture.BeginScope();
        var fdb = final.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var loop = await LoopNodeAsync(fdb, runId);
        loop.Status.ShouldBe(NodeStatus.Success);
        JsonDocument.Parse(loop.OutputsJson).RootElement.GetProperty("iterations").GetInt32().ShouldBe(3);

        // The approval re-ran to Success once per iteration, each under its own iteration key.
        var approvedKeys = await fdb.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == "gate" && n.Status == NodeStatus.Success)
            .Select(n => n.IterationKey).ToListAsync();
        approvedKeys.OrderBy(k => k).ShouldBe(new[] { "loop#0", "loop#1", "loop#2" });
    }

    [Fact]
    public async Task Loop_threads_a_variable_correctly_across_a_body_suspend()
    {
        // The hard property: an approval suspends mid-pass, yet the threaded loop variable is rebuilt
        // from the ledger on every resume, so the body sees the SAME accumulated value it would have
        // without the pause. The probe runs AFTER the approval each pass, recording loop.acc.
        var key = "probe-" + Guid.NewGuid().ToString("N");
        LoopProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ThreadedApprovalLoopDefinition(key, maxIterations: 3));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        for (var pass = 0; pass < 3; pass++)
        {
            await RunEngineAsync(runId);
            (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue($"pass {pass} approval");
        }
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        // Same accumulated sequence the non-suspending threading test produces — proof the replay
        // reconstructed loop.acc faithfully across three suspend/resume cycles.
        LoopProbeNode.SeenFor(key).ShouldBe(new[] { "x", "x:0", "x:0:1" });
        JsonDocument.Parse((await LoopNodeAsync(db, runId)).OutputsJson).RootElement
            .GetProperty("acc").GetString().ShouldBe("x:0:1:2");
    }

    [Fact]
    public async Task Continue_on_error_composes_with_a_body_suspend()
    {
        // The trickiest combination: a loop that BOTH parks on an approval AND skips a failed pass.
        // Body is gate(approval) → boom(fails the first time, succeeds after). errorHandling=continue.
        // pass#0: approve → boom fails → pass skipped. pass#1: approve → boom succeeds. ⇒ two approvals,
        // failedIterations=1, run Success. Proves the failure-replay and the suspend-resume compose.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ContinueWithApprovalLoopDefinition(Guid.NewGuid().ToString("N")));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        for (var pass = 0; pass < 2; pass++)
        {
            await RunEngineAsync(runId);
            (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue($"pass {pass} approval");
        }
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var outputs = JsonDocument.Parse((await LoopNodeAsync(db, runId)).OutputsJson).RootElement;
        outputs.GetProperty("iterations").GetInt32().ShouldBe(2);
        outputs.GetProperty("failedIterations").GetInt32().ShouldBe(1, "pass#0 failed-and-skipped, reconstructed correctly across the suspend");
    }

    [Fact]
    public async Task A_nested_loop_runs_the_full_cross_product_with_distinct_iteration_keys()
    {
        // outer(2) × inner(2): the inner body's probe runs 4 times, once per (outer, inner) pair. The
        // per-iteration keys must nest as "outer#i/inner#j" so the four passes never collide.
        var key = "probe-" + Guid.NewGuid().ToString("N");
        LoopProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, NestedLoopDefinition(key, outerMax: 2, innerMax: 2));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        // The probe saw the INNER index each run, resetting per outer pass (inner loop.* shadows the outer's).
        LoopProbeNode.SeenFor(key).ShouldBe(new[] { "i0", "i1", "i0", "i1" });

        // Each inner-body pass persisted under a nested key — proof the cross product ran without collision.
        var probeKeys = await db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == "probe").Select(n => n.IterationKey).ToListAsync();
        probeKeys.OrderBy(k => k).ShouldBe(new[] { "outer#0/inner#0", "outer#0/inner#1", "outer#1/inner#0", "outer#1/inner#1" });

        // The outer loop ran 2 passes; the inner loop node has a record per outer pass.
        var outer = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "outer" && n.IterationKey == "");
        outer.Status.ShouldBe(NodeStatus.Success);
        JsonDocument.Parse(outer.OutputsJson).RootElement.GetProperty("iterations").GetInt32().ShouldBe(2);
        var innerKeys = await db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == "inner" && n.Status == NodeStatus.Success).Select(n => n.IterationKey).ToListAsync();
        innerKeys.OrderBy(k => k).ShouldBe(new[] { "outer#0", "outer#1" });
    }

    [Fact]
    public async Task An_approval_inside_a_nested_loop_parks_and_resumes_per_inner_iteration()
    {
        // Durable suspend through TWO loop levels: outer(1) × inner(2) with an approval in the inner body.
        // ⇒ two approvals; each resume re-enters outer pass 0 AND inner pass j (recursive rehydration).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, NestedApprovalLoopDefinition(outerMax: 1, innerMax: 2));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        for (var pass = 0; pass < 2; pass++)
        {
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, $"inner pass {pass}: parked on the approval");
                var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
                wait.IterationKey.ShouldBe($"outer#0/inner#{pass}", "the wait is keyed to the nested (outer, inner) iteration");
            }

            (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
        }

        await RunEngineAsync(runId);

        using var final = _fixture.BeginScope();
        var fdb = final.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        var approvedKeys = await fdb.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == "gate" && n.Status == NodeStatus.Success).Select(n => n.IterationKey).ToListAsync();
        approvedKeys.OrderBy(k => k).ShouldBe(new[] { "outer#0/inner#0", "outer#0/inner#1" });
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<Core.Persistence.Entities.WorkflowRunNode> LoopNodeAsync(CodeSpaceDbContext db, Guid runId) =>
        await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "loop" && n.IterationKey == "");

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "loop-" + Guid.NewGuid().ToString("N")[..6],
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

    // Approve the run's currently-pending approval wait (whatever iteration it's parked on) via the
    // real command → service → resume chain, exactly as a human clicking approve would.
    private async Task<bool> ApproveAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = true, Comment = "ok" });
    }

    // manual → loop(body: loop_start → probe) → terminal. acc threads "start" + ":<index>" each pass.
    // Plain (non-interpolated) raw strings keep the literal {{loop.*}} / {{nodes.*}} templates intact;
    // the two scalar params are spliced via Replace (a $$"""…""" string would mis-read {{loop.acc}}).
    private static WorkflowDefinition ConditionLoopDefinition(string probeKey, string terminateAtIndex, int maxIterations)
    {
        var loopConfig = """
            {
              "loopVariables": [ { "name": "acc", "type": "String", "value": "start", "update": "{{loop.acc}}:{{loop.index}}" } ],
              "termination": { "logic": "and", "conditions": [ { "ref": "{{loop.index}}", "op": "eq", "value": "__IDX__" } ] },
              "maxIterations": __MAX__
            }
            """.Replace("__IDX__", terminateAtIndex).Replace("__MAX__", maxIterations.ToString());

        var probeInputs = """{ "key": "__KEY__", "value": "{{loop.acc}}" }""".Replace("__KEY__", probeKey);

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "loop", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json(loopConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "probe", TypeKey = LoopProbeNode.Key, ParentId = "loop",
                        Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(probeInputs) },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{ "iters": "{{nodes.loop.outputs.iterations}}", "acc": "{{nodes.loop.outputs.acc}}" }""") },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "loop" },
                new() { From = "loop", To = "end" },
                new() { From = "ls", To = "probe" },
            },
        };
    }

    // manual → loop(body: loop_start → boom[fails `failTimes` times]; optionally boom =(error)=> caught) → terminal.
    // errorHandling: null/"terminate" (default) ⇒ a body failure fails the loop; "continue" ⇒ it skips the pass.
    private static WorkflowDefinition FailingBodyDefinition(string flakyKey, bool withErrorBranch, string? errorHandling = null, int failTimes = 99, int maxIterations = 3, string terminateAtIndex = "0")
    {
        // Plain raw string + Replace keeps the literal {{loop.index}} template intact (a $-string would mis-read it).
        var ehLine = errorHandling != null ? $"\"errorHandling\": \"{errorHandling}\"," : "";
        var loopConfig = """
            { __EH__ "termination": { "conditions": [ { "ref": "{{loop.index}}", "op": "eq", "value": "__IDX__" } ] }, "maxIterations": __MAX__ }
            """.Replace("__EH__", ehLine).Replace("__IDX__", terminateAtIndex).Replace("__MAX__", maxIterations.ToString());

        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "loop", TypeKey = "flow.loop", Inputs = WorkflowsTestSeed.EmptyJson(), Config = WorkflowsTestSeed.Json(loopConfig) },
            new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "boom", TypeKey = FlakyTestNode.Key, ParentId = "loop",
                    Config = WorkflowsTestSeed.Json($$"""{ "key": "{{flakyKey}}", "failTimes": {{failTimes}} }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        };

        var edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "loop" },
            new() { From = "loop", To = "end" },
            new() { From = "ls", To = "boom" },
        };

        if (withErrorBranch)
        {
            // An always-succeeds handler (FlakyTestNode with failTimes:0) wired to boom's error edge.
            nodes.Add(new() { Id = "caught", TypeKey = FlakyTestNode.Key, ParentId = "loop",
                              Config = WorkflowsTestSeed.Json($$"""{ "key": "{{flakyKey}}-caught", "failTimes": 0 }"""), Inputs = WorkflowsTestSeed.EmptyJson() });
            edges.Add(new() { From = "boom", To = "caught", SourceHandle = WorkflowHandles.Error });
        }

        return new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges };
    }

    // manual → loop(body: loop_start → gate[wait_approval]) → terminal. Runs to the cap; each pass parks
    // on its own approval. The case the user asked about: an approval inside a loop, approved per pass.
    private static WorkflowDefinition ApprovalLoopDefinition(int maxIterations) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "loop", TypeKey = "flow.loop", Inputs = WorkflowsTestSeed.EmptyJson(),
                    Config = WorkflowsTestSeed.Json($$"""{ "maxIterations": {{maxIterations}} }""") },
            new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = "flow.wait_approval", ParentId = "loop",
                    Config = WorkflowsTestSeed.Json("""{ "prompt": "approve iteration?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "loop" },
            new() { From = "loop", To = "end" },
            new() { From = "ls", To = "gate" },
        },
    };

    // manual → loop(body: loop_start → gate[wait_approval] → probe) → terminal. acc threads "x" + ":<index>"
    // each pass; the probe (AFTER the approval) records loop.acc so the test can prove the threaded var is
    // rebuilt correctly across each suspend/resume. Plain raw strings keep the literal {{loop.*}} templates.
    private static WorkflowDefinition ThreadedApprovalLoopDefinition(string probeKey, int maxIterations)
    {
        var loopConfig = """
            { "loopVariables": [ { "name": "acc", "type": "String", "value": "x", "update": "{{loop.acc}}:{{loop.index}}" } ], "maxIterations": __MAX__ }
            """.Replace("__MAX__", maxIterations.ToString());
        var probeInputs = """{ "key": "__KEY__", "value": "{{loop.acc}}" }""".Replace("__KEY__", probeKey);

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "loop", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json(loopConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "gate", TypeKey = "flow.wait_approval", ParentId = "loop",
                        Config = WorkflowsTestSeed.Json("""{ "prompt": "go?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "probe", TypeKey = LoopProbeNode.Key, ParentId = "loop",
                        Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(probeInputs) },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{ "acc": "{{nodes.loop.outputs.acc}}" }""") },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "loop" },
                new() { From = "loop", To = "end" },
                new() { From = "ls", To = "gate" },
                new() { From = "gate", To = "probe" },
            },
        };
    }

    // manual → loop(errorHandling=continue, body: loop_start → gate[approval] → boom[fails once]) → terminal.
    // boom fails the first pass (skipped) then succeeds — exercises continue-on-error THROUGH a suspend.
    private static WorkflowDefinition ContinueWithApprovalLoopDefinition(string flakyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "loop", TypeKey = "flow.loop", Inputs = WorkflowsTestSeed.EmptyJson(),
                    Config = WorkflowsTestSeed.Json("""{ "errorHandling": "continue", "maxIterations": 2 }""") },
            new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = "flow.wait_approval", ParentId = "loop",
                    Config = WorkflowsTestSeed.Json("""{ "prompt": "go?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "boom", TypeKey = FlakyTestNode.Key, ParentId = "loop",
                    Config = WorkflowsTestSeed.Json($$"""{ "key": "{{flakyKey}}", "failTimes": 1 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "loop" },
            new() { From = "loop", To = "end" },
            new() { From = "ls", To = "gate" },
            new() { From = "gate", To = "boom" },
        },
    };

    // manual → outer-loop(body: ls_o → inner-loop(body: ls_i → probe[value={{loop.index}}])) → terminal.
    // The probe records the INNER loop index each innermost pass.
    private static WorkflowDefinition NestedLoopDefinition(string probeKey, int outerMax, int innerMax)
    {
        // "i{{loop.index}}" (literal + template) resolves to a STRING ("i0", "i1", …); a bare
        // "{{loop.index}}" would resolve to the raw JSON number, which the probe reads as "".
        var probeInputs = """{ "key": "__KEY__", "value": "i{{loop.index}}" }""".Replace("__KEY__", probeKey);
        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "outer", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json($$"""{ "maxIterations": {{outerMax}} }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "ls_o", TypeKey = "flow.loop_start", ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "inner", TypeKey = "flow.loop", ParentId = "outer", Config = WorkflowsTestSeed.Json($$"""{ "maxIterations": {{innerMax}} }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "ls_i", TypeKey = "flow.loop_start", ParentId = "inner", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "probe", TypeKey = LoopProbeNode.Key, ParentId = "inner", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(probeInputs) },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "outer" },
                new() { From = "outer", To = "end" },
                new() { From = "ls_o", To = "inner" },
                new() { From = "ls_i", To = "probe" },
            },
        };
    }

    // manual → outer-loop(body: ls_o → inner-loop(body: ls_i → gate[wait_approval])) → terminal.
    private static WorkflowDefinition NestedApprovalLoopDefinition(int outerMax, int innerMax) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "outer", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json($$"""{ "maxIterations": {{outerMax}} }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ls_o", TypeKey = "flow.loop_start", ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "inner", TypeKey = "flow.loop", ParentId = "outer", Config = WorkflowsTestSeed.Json($$"""{ "maxIterations": {{innerMax}} }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ls_i", TypeKey = "flow.loop_start", ParentId = "inner", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = "flow.wait_approval", ParentId = "inner", Config = WorkflowsTestSeed.Json("""{ "prompt": "go?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "outer" },
            new() { From = "outer", To = "end" },
            new() { From = "ls_o", To = "inner" },
            new() { From = "ls_i", To = "gate" },
        },
    };
}
