using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
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
/// THE join the review flagged untested: the headline planner+parallel-agents flow with a REAL <c>agent.code</c>
/// body INSIDE a <c>flow.map</c>. Mirrors <see cref="ParallelAgentResumeFlowTests"/> (the proven real-agent.code
/// top-level parallel-resume test) but the two agents are the map's per-element branches. A 2-element flow.map
/// whose body is the REAL <c>agent.code</c> node fans out → both branches park REAL <c>AgentRun</c> waits under
/// <c>&lt;mapId&gt;#0</c> / <c>&lt;mapId&gt;#1</c> → each resumes via the REAL completion notifier
/// (<see cref="WorkflowResumeAgentRunCompletionNotifier"/> / <see cref="IWorkflowResumeService.ResumeOnWaitCompletionAsync"/>).
/// Pins: results[] ORDERED by element index; exactly-once per branch (each AgentRun consumed once + the
/// wait-for-all barrier holds the run Suspended until the LAST agent completes); NO cross-branch contamination
/// (each branch resumes with its OWN agent's result, never the first completer's).
///
/// <para>Same fidelity model as <see cref="ParallelAgentResumeFlowTests"/> (medium-mock, Rule 12 🟡): every wiring
/// class is real — the engine, AgentRunService, the completion notifier, real Postgres AgentRun + WorkflowRunWait
/// rows — only the sandboxed CLI is stood in for by <see cref="SimulateAgentCompletionAsync"/> (the job client's
/// AutoExecute is off so the binary-less harness never runs).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MapAgentResumeFlowTests
{
    private readonly PostgresFixture _fixture;

    public MapAgentResumeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_flow_map_over_real_agent_code_branches_resumes_each_with_its_own_result_ordered_and_exactly_once()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, MapOverAgentCodeDefinition());
        // Two elements → two parallel map branches, each running the REAL agent.code body.
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b"] }""");

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // record dispatches; the binary-less harness must not run

        try
        {
            // ── Pass 1: both branches' agent.code nodes park in one map wave → two AgentRun waits, run Suspended. ──
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);

                var waits = await db.WorkflowRunWait.AsNoTracking()
                    .Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending && w.WaitKind == WorkflowWaitKinds.AgentRun)
                    .Select(w => w.IterationKey).ToListAsync();
                waits.Count.ShouldBe(2, "each map branch parks its own AgentRun wait under '<mapId>#<i>'");
                waits.OrderBy(k => k).ShouldBe(new[] { "map#0", "map#1" });   // the two branch waits are keyed by element index
            }

            // Resolve the AgentRun id of each branch by its iteration key — branch 0 = element "a", branch 1 = "b".
            var agent0 = await BranchAgentRunIdAsync(runId, "map#0");
            var agent1 = await BranchAgentRunIdAsync(runId, "map#1");
            agent0.ShouldNotBe(agent1, "each parallel branch staged its OWN distinct AgentRun");

            // dispatch-all: BOTH branch agent runs were enqueued to the executor on suspend.
            var dispatched = jobClient.Calls
                .Where(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync))
                .Select(c => c.RunId).ToList();
            dispatched.ShouldContain(agent0);
            dispatched.ShouldContain(agent1);

            // ── Branch 1 (element "b") completes FIRST, with its OWN distinct result. ──
            await SimulateAgentCompletionAsync(agent1, "RESULT-B", "agent/branch-b");

            // Exactly-once + wait-for-all barrier: branch 1's completion resolves ONLY its wait; the run stays Suspended.
            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "one of two parallel branch agents finishing does NOT advance the map");

                var wait0 = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Token == agent0.ToString());
                var wait1 = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Token == agent1.ToString());
                wait0.Status.ShouldBe(WorkflowWaitStatuses.Pending, "branch 0's wait is UNTOUCHED by branch 1's completion (no cross-branch contamination)");
                wait1.Status.ShouldBe(WorkflowWaitStatuses.Resolved, "branch 1's own wait is resolved");
                wait1.PayloadJson!.ShouldContain("RESULT-B");
            }

            // ── Branch 0 (element "a") completes with its OWN distinct result → the last wait → the map advances. ──
            await SimulateAgentCompletionAsync(agent0, "RESULT-A", "agent/branch-a");
            await RunEngineAsync(runId);   // resume pass — one re-walk consumes both resolved waits + reduces

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

                // The crux: results[] is ORDERED by element index, and each branch carries ITS OWN agent's result —
                // never the first completer's (branch 1 "b" finished first, but results[0] is still branch 0 "a").
                var outputs = JsonDocument.Parse((await MapNodeAsync(db, runId)).OutputsJson).RootElement;
                outputs.GetProperty("count").GetInt32().ShouldBe(2);
                outputs.GetProperty("failed").GetInt32().ShouldBe(0);

                var results = outputs.GetProperty("results");
                results.GetArrayLength().ShouldBe(2);
                results[0].GetProperty("summary").GetString().ShouldBe("RESULT-A", "results[0] is branch 0 (element 'a'), ordered by index — NOT the first completer");
                results[1].GetProperty("summary").GetString().ShouldBe("RESULT-B", "results[1] is branch 1 (element 'b'), each branch its own result — no cross-contamination");

                // Exactly-once: each AgentRun was consumed once; no branch re-dispatched on the resume re-walk.
                var redispatch = jobClient.Calls
                    .Count(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync));
                redispatch.ShouldBe(2, "each branch's agent run was dispatched EXACTLY once across the whole flow — no re-dispatch on resume");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_queued_branch_agent_run_under_a_cancelled_parent_run_is_cancelled_by_the_reconciler_not_relaunched()
    {
        // THE RECONCILER PARENT-RUN-TERMINAL GUARD (the real PR-D1 bug): two map branches stage Queued AgentRuns,
        // the run suspends — then the PARENT workflow run is CANCELLED (operator cancel / a terminate-mode sibling
        // failure) while the branch dispatch was lost in the crash window. On the OLD reconciler, the stuck-Queued
        // sweep would LAUNCH a sandbox/executor for an already-dead workflow. The guard must instead CANCEL each
        // orphaned Queued branch run and NEVER re-dispatch it. Drives the REAL reconciler against real rows.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, MapOverAgentCodeDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b"] }""");

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            // Pass 1: both branches park Queued AgentRun waits, the run suspends.
            await RunEngineAsync(runId);

            var agent0 = await BranchAgentRunIdAsync(runId, "map#0");
            var agent1 = await BranchAgentRunIdAsync(runId, "map#1");

            // The parent run lands CANCELLED (operator cancel) and the branch runs sit stuck-Queued past the
            // liveness window — the exact orphan signature the reconciler's re-dispatch sweep used to relaunch.
            await SetRunStatusAsync(runId, WorkflowRunStatus.Cancelled);
            await BackdateAgentRunCreatedAsync(new[] { agent0, agent1 }, AgentRunLiveness.Window + TimeSpan.FromMinutes(5));

            jobClient.Clear();   // forget pass-1 dispatches; only the reconciler's actions matter now

            await ReconcileAsync();

            // Assert on THIS run's specific agents (the shared DB means a concurrent test's runs can also be swept,
            // so the global summary count isn't this run's signal). The guard cancelled BOTH orphaned branch runs and
            // re-dispatched NEITHER — no executor was enqueued for either of OUR branch ids.
            jobClient.Calls.Where(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync))
                .Select(c => c.RunId).ShouldNotContain(agent0, "no executor/sandbox launched for branch 0 of an already-cancelled workflow");
            jobClient.Calls.Where(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync))
                .Select(c => c.RunId).ShouldNotContain(agent1, "no executor/sandbox launched for branch 1 of an already-cancelled workflow");

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();
            foreach (var id in new[] { agent0, agent1 })
                (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == id)).Status
                    .ShouldBe(AgentRunStatus.Cancelled, "the orphaned Queued branch run was cancelled (a terminal state), not left Queued forever");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_queued_branch_agent_run_under_a_still_suspended_parent_is_re_dispatched_as_before()
    {
        // NON-BREAKING companion: the normal durable-recovery path is PRESERVED. Same shape, but the parent run
        // stays SUSPENDED (the legitimate parked state). A stuck-Queued branch whose dispatch was lost must STILL
        // be re-dispatched — exactly as before the guard. The guard only fires for a TERMINAL parent.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, MapOverAgentCodeDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a"] }""");

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);   // one branch parks a Queued AgentRun wait; the parent suspends

            var agent0 = await BranchAgentRunIdAsync(runId, "map#0");

            // Parent stays Suspended (the live parked state); the branch run is stuck-Queued past the window.
            await BackdateAgentRunCreatedAsync(new[] { agent0 }, AgentRunLiveness.Window + TimeSpan.FromMinutes(5));

            jobClient.Clear();

            await ReconcileAsync();

            // Assert on THIS run's specific agent (the shared DB means the global summary count can include a
            // concurrent test's runs). The executor WAS re-enqueued for our live-parent branch run — the unchanged
            // durable-recovery path the guard must NOT break.
            jobClient.Calls.Where(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync))
                .Select(c => c.RunId).ShouldContain(agent0, "the executor was re-enqueued for the live-parent branch run");

            using var verify = _fixture.BeginScope();
            (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == agent0)).Status
                .ShouldBe(AgentRunStatus.Queued, "the branch run stays Queued (awaiting its re-dispatched executor) — NOT cancelled");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<Core.Persistence.Entities.WorkflowRunNode> MapNodeAsync(CodeSpaceDbContext db, Guid runId) =>
        await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");

    private async Task<AgentRunReconcileSummary> ReconcileAsync()
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);
    }

    // Flip the parent workflow run's status directly (the operator-cancel / terminate-failure end state) — a pure
    // UPDATE so the audit interceptor doesn't refuse the status change on a tracked entity.
    private async Task SetRunStatusAsync(Guid runId, WorkflowRunStatus status)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().WorkflowRun
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status));
    }

    // Backdate the branch agent runs' CreatedDate past the liveness window so they look stuck-Queued to the
    // reconciler's stale-window check. ExecuteUpdate bypasses the audit interceptor's CreatedDate freeze.
    private async Task BackdateAgentRunCreatedAsync(IEnumerable<Guid> agentRunIds, TimeSpan ago)
    {
        var ids = agentRunIds.ToList();
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().AgentRun
            .Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedDate, DateTimeOffset.UtcNow - ago));
    }

    // Map a branch's iteration key → its AgentRun id, via the branch's pending AgentRun wait Token (== the agent run id).
    private async Task<Guid> BranchAgentRunIdAsync(Guid runId, string iterationKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var token = await db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.IterationKey == iterationKey && w.WaitKind == WorkflowWaitKinds.AgentRun)
            .Select(w => w.Token).SingleAsync();
        return Guid.Parse(token);
    }

    // Drive the executor's terminal sequence (MarkRunning → Complete → Notify) without the sandboxed CLI —
    // the exact path AgentRunExecutor follows on a real completion (mirrors ParallelAgentResumeFlowTests).
    private async Task SimulateAgentCompletionAsync(Guid agentRunId, string summary, string branch)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var notifier = scope.Resolve<IAgentRunCompletionNotifier>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ProducedBranch = branch,
        }, CancellationToken.None);
        await notifier.NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "map-agents-" + Guid.NewGuid().ToString("N")[..6],
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

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    // manual → map(items={{trigger.things}}; body: ms → agent[REAL agent.code, read-only, analysis-only]) → synthesizer.
    // Each branch's agent.code parks a real AgentRun wait; on resume its { summary } reduces into results[i].
    private static WorkflowDefinition MapOverAgentCodeDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.code", ParentId = "map",
                    Config = WorkflowsTestSeed.Json("""{"goal":"Work on {{item}}","harness":"codex-cli","model":"gpt-5.3-codex","runnerKind":"local","readOnly":true}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "agent" },
        },
    };
}
