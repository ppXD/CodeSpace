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

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<Core.Persistence.Entities.WorkflowRunNode> NodeAsync(CodeSpaceDbContext db, Guid runId, string nodeId) =>
        await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == nodeId && n.IterationKey == "");

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

    // Literal {gate,party} values (no {{}} templates) — spliced via Replace to dodge the raw-string brace trap.
    private static JsonElement ProbeInputs(string gate, string party) =>
        WorkflowsTestSeed.Json("""{ "gate": "__GATE__", "party": "__PARTY__" }""".Replace("__GATE__", gate).Replace("__PARTY__", party));
}
