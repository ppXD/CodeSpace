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
/// Engine v2 Phase 4 — intra-run concurrency (in-process parallel wave), against real Postgres + the
/// real engine. The walker drains each ready frontier and runs the independent regular nodes
/// concurrently (each in its OWN DI scope ⇒ own DbContext / record logger), merging their effects
/// single-threaded. These tests PROVE the parallelism is real (a barrier the wave must clear together,
/// which a sequential walk could only deadlock on) and that it composes correctly with fan-in,
/// per-node scope isolation, an unhandled failure, an error edge, and a suspend.
///
/// <para>They assume the engine's default max-parallelism (8) ≥ the fan-out width each test arms (≤6).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ConcurrentWaveFlowTests
{
    private readonly PostgresFixture _fixture;

    public ConcurrentWaveFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Independent_branches_run_concurrently_and_fan_in_waits_for_all()
    {
        var gate = "gate-" + Guid.NewGuid().ToString("N");
        var parties = new[] { "a", "b", "c" };
        ConcurrencyProbeNode.Arm(gate, parties.Length);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FanOutDefinition(gate, parties));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        // The barrier only releases if all 3 branches were in-flight at once — a sequential walk would
        // have timed out at it. This is the hard proof the frontier ran concurrently.
        ConcurrencyProbeNode.AllArrived(gate).ShouldBeTrue("all branches must reach the barrier together — proves real concurrency");
        ConcurrencyProbeNode.Peak(gate).ShouldBe(parties.Length, "peak simultaneous in-flight branches == the fan-out width");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);

        foreach (var p in parties)
            (await NodeAsync(db, runId, p)).Status.ShouldBe(NodeStatus.Success);

        // Fan-in: the terminal ran ONLY after every branch settled, and its inputs resolved against the
        // merged scope of all three — so each branch's output was present. Deterministic regardless of
        // which task finished first.
        JsonDocument.Parse(run.OutputsJson).RootElement.GetProperty("all").GetString().ShouldBe("a-b-c");
    }

    [Fact]
    public async Task Wide_fan_out_keeps_each_node_in_its_own_scope_no_dbcontext_race()
    {
        // Six branches at once: each runs in its own DI scope (own CodeSpaceDbContext + record logger).
        // If they shared one DbContext, EF would throw "a second operation started on this context" and
        // some node records would be missing / the run would fail. All six landing Success — with a peak
        // of 6 simultaneously in-flight — IS the per-node scope-isolation assertion.
        var gate = "gate-" + Guid.NewGuid().ToString("N");
        var parties = new[] { "a", "b", "c", "d", "e", "f" };
        ConcurrencyProbeNode.Arm(gate, parties.Length);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FanOutDefinition(gate, parties));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        ConcurrencyProbeNode.AllArrived(gate).ShouldBeTrue();
        ConcurrencyProbeNode.Peak(gate).ShouldBe(parties.Length, "all six branches were simultaneously in-flight, each with its own scope");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        // Every parallel node persisted a clean node.completed via its own scope's record logger.
        var completed = await db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && parties.Contains(n.NodeId) && n.Status == NodeStatus.Success)
            .Select(n => n.NodeId).ToListAsync();
        completed.OrderBy(x => x).ShouldBe(parties);
    }

    [Fact]
    public async Task One_branch_failing_with_no_error_edge_fails_the_run_but_siblings_still_complete()
    {
        // Parallel semantics (intended): independent siblings already in-flight run to completion even
        // when another branch hard-fails — then the unhandled failure fails the run. (The old sequential
        // walk's "which siblings run before a failure" was order-arbitrary; concurrency makes it "all".)
        var gate = "gate-" + Guid.NewGuid().ToString("N");
        var parties = new[] { "a", "b" };
        ConcurrencyProbeNode.Arm(gate, parties.Length);
        var boomKey = "boom-" + Guid.NewGuid().ToString("N");

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FanOutWithFailingBranchDefinition(gate, parties, boomKey, withErrorEdge: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        ConcurrencyProbeNode.AllArrived(gate).ShouldBeTrue("the OK siblings ran concurrently alongside the failing branch");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Failure, "an unhandled branch failure fails the run");
        (await NodeAsync(db, runId, "boom")).Status.ShouldBe(NodeStatus.Failure);
        foreach (var p in parties)
            (await NodeAsync(db, runId, p)).Status.ShouldBe(NodeStatus.Success, "the in-flight sibling completed before the run failed");
    }

    [Fact]
    public async Task A_failing_branch_with_an_error_edge_is_handled_and_the_run_succeeds()
    {
        // Phase 2 (error routing) composes with the Phase 4 wave: a branch that fails inside the parallel
        // batch routes down its own `error` edge to a handler, so the run completes successfully.
        var gate = "gate-" + Guid.NewGuid().ToString("N");
        var parties = new[] { "a", "b" };
        ConcurrencyProbeNode.Arm(gate, parties.Length);
        var boomKey = "boom-" + Guid.NewGuid().ToString("N");

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FanOutWithFailingBranchDefinition(gate, parties, boomKey, withErrorEdge: true));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "the error edge handled the in-wave failure");
        (await NodeAsync(db, runId, "boom")).Status.ShouldBe(NodeStatus.Failure, "boom still failed — it was just routed, not un-failed");
        (await NodeAsync(db, runId, "handler")).Status.ShouldBe(NodeStatus.Success);
        foreach (var p in parties)
            (await NodeAsync(db, runId, p)).Status.ShouldBe(NodeStatus.Success);
    }

    [Fact]
    public async Task A_suspend_in_a_wave_parks_the_run_while_a_sibling_still_completes_then_resumes()
    {
        // One branch suspends on an approval while a sibling runs in the SAME wave. The sibling completes
        // (its record is persisted by its own scope) and the run parks Suspended; approving resumes it to
        // Success. Proves suspend-in-a-parallel-wave is durable and doesn't strand the sibling.
        var gate = "gate-" + Guid.NewGuid().ToString("N");
        ConcurrencyProbeNode.Arm(gate, 1);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FanOutWithApprovalDefinition(gate));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using (var parked = _fixture.BeginScope())
        {
            var db = parked.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the approval branch parked the run");
            (await NodeAsync(db, runId, "sib")).Status.ShouldBe(NodeStatus.Success, "the sibling ran in the same wave and completed");
            var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.Approval);
        }

        (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await NodeAsync(fdb, runId, "gate")).Status.ShouldBe(NodeStatus.Success);
    }

    [Fact]
    public async Task Resume_after_a_wave_suspend_does_not_re_execute_completed_siblings()
    {
        // Durability × parallelism — THE guarantee: a branch suspends in a fan-out wave while its
        // siblings complete; on resume the durable walker rehydrates from the ledger and re-runs ONLY
        // the suspended branch, never the already-finished siblings. Proven by each sibling carrying
        // exactly ONE node.started record across the park + resume (a re-run would emit a second).
        var gate = "gate-" + Guid.NewGuid().ToString("N");
        var parties = new[] { "a", "b" };
        ConcurrencyProbeNode.Arm(gate, parties.Length);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FanOutWithApprovalAndSiblingsDefinition(gate, parties));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        ConcurrencyProbeNode.AllArrived(gate).ShouldBeTrue("the siblings ran concurrently alongside the suspending branch");

        using (var parked = _fixture.BeginScope())
        {
            var db = parked.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            foreach (var p in parties)
            {
                (await NodeAsync(db, runId, p)).Status.ShouldBe(NodeStatus.Success);
                (await StartedCountAsync(db, runId, p)).ShouldBe(1, $"{p} ran exactly once before the suspend");
            }
        }

        (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await NodeAsync(fdb, runId, "gate")).Status.ShouldBe(NodeStatus.Success);
        foreach (var p in parties)
            (await StartedCountAsync(fdb, runId, p)).ShouldBe(1, $"{p} was NOT re-executed on resume — rehydration skipped the completed sibling");
    }

    [Fact]
    public async Task Two_parallel_approval_branches_each_resolve_independently_then_the_run_completes()
    {
        // Two branches in the SAME wave both suspend on their OWN flow.wait_approval (two pending waits
        // under one run). Each gate is a DISTINCT decision: the run-level approve resolves exactly ONE
        // pending approval at a time and must NOT collapse both into a single click. So the run re-parks
        // after the first approve (one wait still pending) and completes only after the second — durable
        // double-suspend that resolves wait-by-wait, never run-wide.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FanOutTwoApprovalsDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using (var parked = _fixture.BeginScope())
        {
            var db = parked.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending))
                .ShouldBe(2, "both parallel approval branches parked their own wait");
        }

        // First approve resolves ONE gate; the sibling gate is untouched, so the run re-parks.
        (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
        await RunEngineAsync(runId);

        using (var midway = _fixture.BeginScope())
        {
            var db = midway.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "one approve resolves one gate — the other still gates the run");
            (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending))
                .ShouldBe(1, "exactly one approval wait remains — the first approve did NOT collapse both");
        }

        // Second approve resolves the remaining gate; now both branches fan in and the run completes.
        (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await NodeAsync(fdb, runId, "gx")).Status.ShouldBe(NodeStatus.Success);
        (await NodeAsync(fdb, runId, "gy")).Status.ShouldBe(NodeStatus.Success);
    }

    [Fact]
    public async Task A_loop_and_parallel_branches_share_a_wave_and_all_fan_in()
    {
        // A loop container and two regular branches sit in ONE ready frontier. The walker runs the
        // regular branches as a concurrent batch and the loop sequentially (engine-driven), then the
        // terminal fans in over all three. Pins that loops coexist with the parallel batch (the wave
        // partition keeps loops off the Task.WhenAll) without deadlock or lost output.
        var gate = "gate-" + Guid.NewGuid().ToString("N");
        var parties = new[] { "a", "b" };
        ConcurrencyProbeNode.Arm(gate, parties.Length);
        var bodyKey = "body-" + Guid.NewGuid().ToString("N");
        LoopProbeNode.Reset(bodyKey);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FanOutWithLoopDefinition(gate, bodyKey));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        ConcurrencyProbeNode.AllArrived(gate).ShouldBeTrue("the two regular branches ran concurrently while the loop ran in the same frontier");
        ConcurrencyProbeNode.Peak(gate).ShouldBe(parties.Length);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);

        var loop = await NodeAsync(db, runId, "loop");
        loop.Status.ShouldBe(NodeStatus.Success);
        JsonDocument.Parse(loop.OutputsJson).RootElement.GetProperty("iterations").GetInt32().ShouldBe(2, "the loop ran its 2 passes sequentially");
        LoopProbeNode.SeenFor(bodyKey).ShouldBe(new[] { "i0", "i1" }, "the loop body ran once per pass");

        foreach (var p in parties)
            (await NodeAsync(db, runId, p)).Status.ShouldBe(NodeStatus.Success);

        // Fan-in saw the loop's output AND both parallel branches' outputs.
        var outputs = JsonDocument.Parse(run.OutputsJson).RootElement;
        outputs.GetProperty("iters").GetInt32().ShouldBe(2);
        outputs.GetProperty("ab").GetString().ShouldBe("a-b");
    }

    [Fact]
    public async Task Two_failing_branches_in_a_wave_route_their_error_edges_into_one_shared_handler()
    {
        // Error routing × parallel fan-in: two branches fail concurrently in the wave, each routes down
        // its own `error` edge to ONE shared handler. The handler runs EXACTLY once (fan-in waits for
        // both) and the run succeeds — proving multiple live error edges converge correctly under
        // parallel execution.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ErrorFanInDefinition(
            "b1-" + Guid.NewGuid().ToString("N"), "b2-" + Guid.NewGuid().ToString("N"), "h-" + Guid.NewGuid().ToString("N")));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "both failures were handled by the shared error branch");
        (await NodeAsync(db, runId, "boom1")).Status.ShouldBe(NodeStatus.Failure);
        (await NodeAsync(db, runId, "boom2")).Status.ShouldBe(NodeStatus.Failure);
        // NodeAsync uses Single() — it throws if the handler ran zero or more than once, so this pins
        // "exactly one handler execution" (the fan-in deduped the two converging error edges).
        (await NodeAsync(db, runId, "handler")).Status.ShouldBe(NodeStatus.Success);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<Core.Persistence.Entities.WorkflowRunNode> NodeAsync(CodeSpaceDbContext db, Guid runId, string nodeId) =>
        await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == nodeId && n.IterationKey == "");

    private static async Task<int> StartedCountAsync(CodeSpaceDbContext db, Guid runId, string nodeId) =>
        await db.WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.RecordType == WorkflowRunRecordTypes.NodeStarted);

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "wave-" + Guid.NewGuid().ToString("N")[..6],
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

    private async Task<bool> ApproveAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = true, Comment = "ok" });
    }

    // manual → [probe per party, all barriered on `gate`] → terminal. The terminal references EVERY
    // branch's output so a successful resolve proves all branches' effects merged before the fan-in ran.
    private static WorkflowDefinition FanOutDefinition(string gate, IReadOnlyList<string> parties)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        };
        var edges = new List<EdgeDefinition>();

        foreach (var p in parties)
        {
            nodes.Add(new() { Id = p, TypeKey = ConcurrencyProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = ProbeInputs(gate, p) });
            edges.Add(new() { From = "start", To = p });
            edges.Add(new() { From = p, To = "end" });
        }

        var allRef = string.Join("-", parties.Select(p => "{{nodes." + p + ".outputs.party}}"));
        var endInputs = """{ "all": "__ALL__" }""".Replace("__ALL__", allRef);
        nodes.Add(new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(endInputs) });

        return new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges };
    }

    // manual → [probes barriered on `gate`, plus boom(flaky, always fails)] → terminal. withErrorEdge:
    // false ⇒ boom has no error edge (fails the run); true ⇒ boom =(error)=> handler(succeeds) → terminal.
    private static WorkflowDefinition FanOutWithFailingBranchDefinition(string gate, IReadOnlyList<string> parties, string boomKey, bool withErrorEdge)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "boom", TypeKey = FlakyTestNode.Key, Inputs = WorkflowsTestSeed.EmptyJson(),
                    Config = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "failTimes": 99 }""".Replace("__KEY__", boomKey)) },
        };
        var edges = new List<EdgeDefinition> { new() { From = "start", To = "boom" } };

        foreach (var p in parties)
        {
            nodes.Add(new() { Id = p, TypeKey = ConcurrencyProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = ProbeInputs(gate, p) });
            edges.Add(new() { From = "start", To = p });
            edges.Add(new() { From = p, To = "end" });
        }

        if (withErrorEdge)
        {
            nodes.Add(new() { Id = "handler", TypeKey = FlakyTestNode.Key, Inputs = WorkflowsTestSeed.EmptyJson(),
                    Config = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "failTimes": 0 }""".Replace("__KEY__", boomKey + "-handler")) });
            edges.Add(new() { From = "boom", To = "handler", SourceHandle = WorkflowHandles.Error });
            edges.Add(new() { From = "handler", To = "end" });
        }

        nodes.Add(new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() });
        return new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges };
    }

    // manual → [gate(wait_approval), sib(probe)] → terminal. The two run in one wave: sib completes while
    // gate suspends. (parties=1 — sib just records that it ran; the assertion is its persisted Success.)
    private static WorkflowDefinition FanOutWithApprovalDefinition(string gate) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.Json("""{ "prompt": "go?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sib", TypeKey = ConcurrencyProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = ProbeInputs(gate, "sib") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "gate" },
            new() { From = "start", To = "sib" },
            new() { From = "gate", To = "end" },
            new() { From = "sib", To = "end" },
        },
    };

    // manual → [gate(wait_approval), one probe per party] → terminal. The probes complete in the wave
    // while gate suspends; on resume the durable walker must re-run ONLY gate, not the probes.
    private static WorkflowDefinition FanOutWithApprovalAndSiblingsDefinition(string gate, IReadOnlyList<string> parties)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.Json("""{ "prompt": "go?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        };
        var edges = new List<EdgeDefinition> { new() { From = "start", To = "gate" }, new() { From = "gate", To = "end" } };

        foreach (var p in parties)
        {
            nodes.Add(new() { Id = p, TypeKey = ConcurrencyProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = ProbeInputs(gate, p) });
            edges.Add(new() { From = "start", To = p });
            edges.Add(new() { From = p, To = "end" });
        }

        return new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges };
    }

    // manual → [gx(wait_approval), gy(wait_approval)] → terminal. Both suspend in ONE wave.
    private static WorkflowDefinition FanOutTwoApprovalsDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gx", TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.Json("""{ "prompt": "x?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gy", TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.Json("""{ "prompt": "y?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "gx" },
            new() { From = "start", To = "gy" },
            new() { From = "gx", To = "end" },
            new() { From = "gy", To = "end" },
        },
    };

    // manual → [loop(2 passes), a(probe), b(probe)] → terminal. The loop is engine-driven (sequential);
    // a + b run as the parallel batch; the terminal references all three so fan-in correctness is pinned.
    private static WorkflowDefinition FanOutWithLoopDefinition(string gate, string loopProbeKey)
    {
        var bodyInputs = """{ "key": "__KEY__", "value": "i{{loop.index}}" }""".Replace("__KEY__", loopProbeKey);
        var endInputs = """{ "iters": "{{nodes.loop.outputs.iterations}}", "ab": "__AB__" }""".Replace("__AB__", "{{nodes.a.outputs.party}}-{{nodes.b.outputs.party}}");

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "loop", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json("""{ "maxIterations": 2 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "bodyprobe", TypeKey = LoopProbeNode.Key, ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(bodyInputs) },
                new() { Id = "a", TypeKey = ConcurrencyProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = ProbeInputs(gate, "a") },
                new() { Id = "b", TypeKey = ConcurrencyProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = ProbeInputs(gate, "b") },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(endInputs) },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "loop" },
                new() { From = "start", To = "a" },
                new() { From = "start", To = "b" },
                new() { From = "loop", To = "end" },
                new() { From = "a", To = "end" },
                new() { From = "b", To = "end" },
                new() { From = "ls", To = "bodyprobe" },
            },
        };
    }

    // Literal {gate,party} values (no {{}} templates) — spliced via Replace to dodge the raw-string brace trap.
    private static JsonElement ProbeInputs(string gate, string party) =>
        WorkflowsTestSeed.Json("""{ "gate": "__GATE__", "party": "__PARTY__" }""".Replace("__GATE__", gate).Replace("__PARTY__", party));

    // manual → {boom1, boom2}(both always fail); boom1 =(error)=> handler <=(error)= boom2; handler → terminal.
    private static WorkflowDefinition ErrorFanInDefinition(string boom1Key, string boom2Key, string handlerKey)
    {
        NodeDefinition Flaky(string id, string key, int failTimes) => new()
        {
            Id = id, TypeKey = FlakyTestNode.Key, Inputs = WorkflowsTestSeed.EmptyJson(),
            Config = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "failTimes": __FT__ }""".Replace("__KEY__", key).Replace("__FT__", failTimes.ToString())),
        };

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                Flaky("boom1", boom1Key, 99),
                Flaky("boom2", boom2Key, 99),
                Flaky("handler", handlerKey, 0),
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "boom1" },
                new() { From = "start", To = "boom2" },
                new() { From = "boom1", To = "handler", SourceHandle = WorkflowHandles.Error },
                new() { From = "boom2", To = "handler", SourceHandle = WorkflowHandles.Error },
                new() { From = "handler", To = "end" },
            },
        };
    }
}
