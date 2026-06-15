using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
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
/// 🟢 THE PR-E E3 CROWN JEWEL (high fidelity — REAL engine + REAL <see cref="SupervisorTurnService"/> +
/// <see cref="RealSupervisorActionExecutor"/> + REAL <see cref="AgentRunService"/> + REAL completion notifier /
/// barrier over real Postgres; the scripted decider stands in for the LLM, the agent completion is simulated
/// like <see cref="MapAgentResumeFlowTests"/> — no real CLI). The supervisor lane DRIVES real agents
/// end-to-end:
/// <list type="bullet">
///   <item>a flag-on <c>agent.supervisor</c> run: turn 0 = plan(2 subtasks) → SELF-ADVANCES; turn 1 =
///         spawn[both] → stages 2 REAL agent runs + parks 2 <c>AgentRun</c> waits keyed <c>#turn1#0/#turn1#1</c>;
///         the WAIT-FOR-ALL barrier holds the supervisor Suspended until BOTH agents complete, then resumes →
///         turn 2 = stop → run Success. The ledger has plan/spawn/stop in order; the 2 spawned AgentRun rows
///         exist + are terminal.</item>
///   <item>RESTART-MID-SPAWN replay: drive to the spawn park, simulate a re-dispatch of the Suspended run
///         (the lost post-commit window), assert NO double-spawn — still exactly 2 agent runs (the E1 claim
///         hop replays the settled spawn, never re-stages).</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorSpawnFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string? _flagBefore;

    public SupervisorSpawnFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _flagBefore = Environment.GetEnvironmentVariable(SupervisorLane.EnabledEnvVar);
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanSpawnStop();   // the E3 arc: plan → spawn → stop
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, _flagBefore);
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task Supervisor_plans_then_spawns_two_real_agents_then_the_barrier_resumes_to_stop()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // the binary-less harness must not run; we simulate completion below

        try
        {
            // ── Turn 0: plan (synchronous) + self-advance. ──
            await RunEngineAsync(runId);
            (await LedgerKinds(runId, teamId)).ShouldBe(new[] { SupervisorDecisionKinds.Plan }, "turn 0 recorded the plan");

            // Self-advance → turn 1 (spawn). With AutoExecute off (the binary-less harness must not run), the
            // post-commit re-dispatch enqueue is record-only, so resolve the turn-0 self-advance wait directly
            // (the exact entry point the engine enqueues), then drive the engine to run turn 1 (which spawns).
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            Guid agent0, agent1;
            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "the spawn turn parks on the staged agent waits — NOT a self-advance");

                var agentWaits = await db.WorkflowRunWait.AsNoTracking()
                    .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
                    .OrderBy(w => w.IterationKey).ToListAsync();
                agentWaits.Count.ShouldBe(2, "spawn[both] staged exactly 2 real AgentRun waits");
                agentWaits.Select(w => w.IterationKey).ShouldBe(new[] { "sup#turn1#0", "sup#turn1#1" }, "the per-turn-per-spawn keys <nodeId>#turn{N}#{k}");

                // No self-advance SupervisorDecision wait for the spawn turn — the agents drive the resume.
                (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision && w.Status == WorkflowWaitStatuses.Pending))
                    .ShouldBe(0, "the spawn turn does NOT park a self-advance wait — only the agent waits");

                agent0 = Guid.Parse(agentWaits[0].Token);
                agent1 = Guid.Parse(agentWaits[1].Token);
                agent0.ShouldNotBe(agent1);

                // Both spawned agent runs exist (Queued), team-inherited, linked to the supervisor run + node.
                var spawned = await db.AgentRun.AsNoTracking().Where(r => r.Id == agent0 || r.Id == agent1).ToListAsync();
                spawned.Count.ShouldBe(2);
                spawned.ShouldAllBe(r => r.TeamId == teamId && r.WorkflowRunId == runId && r.NodeId == "sup" && r.Status == AgentRunStatus.Queued);

                // Both were dispatched to the executor on suspend (the post-commit DispatchPendingAgentRunAsync).
                var dispatched = jobClient.Calls.Where(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync)).Select(c => c.RunId).ToList();
                dispatched.ShouldContain(agent0);
                dispatched.ShouldContain(agent1);
            }

            // ── Agent 1 completes FIRST: the wait-for-all barrier holds the supervisor Suspended. ──
            await SimulateAgentCompletionAsync(agent1, "BETA-DONE");

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "one of two spawned agents finishing does NOT advance the supervisor (the barrier)");
                (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Token == agent0.ToString())).Status
                    .ShouldBe(WorkflowWaitStatuses.Pending, "agent 0's wait is untouched by agent 1's completion");
            }

            // ── Agent 0 completes: the LAST wait → the barrier resolves it + flips the run Pending. ──
            await SimulateAgentCompletionAsync(agent0, "ALPHA-DONE");
            // The barrier's re-dispatch enqueue is record-only (AutoExecute off); drive the engine to run turn 2.
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Success, "once both agents completed, the supervisor resumed → turn 2 stop → the run completes");

                (await LedgerKinds(runId, teamId)).ShouldBe(
                    new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Stop },
                    "the decision ledger has plan/spawn/stop in Sequence order");

                // The spawned agent rows are terminal (the simulated completion drove them Succeeded).
                var spawned = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
                spawned.Count.ShouldBe(2, "exactly the two spawned agent runs — no extra runs");
                spawned.ShouldAllBe(r => r.Status == AgentRunStatus.Succeeded);
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_restart_mid_spawn_does_not_double_spawn()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            // Drive to the spawn park: turn 0 plan → self-advance → turn 1 spawn stages 2 agents + parks.
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            int agentsAfterSpawn;
            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                agentsAfterSpawn = await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId);
                agentsAfterSpawn.ShouldBe(2, "the spawn staged exactly 2 agent runs");
                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            }

            // SIMULATE A RESTART MID-SPAWN: the agents never completed (no SimulateAgentCompletion). Re-dispatch
            // the still-Suspended run as a crash-recovery would. The supervisor node re-enters: the spawn turn's
            // decision is a SETTLED terminal ledger row, so the rehydrate REPLAYS it (TurnNumber advances) and the
            // claim hop never re-runs the spawn side effect → NO new agent runs. The run re-suspends on the SAME
            // 2 agent waits (still pending).
            using (var scope = _fixture.BeginScope())
            {
                // Flip Suspended → Enqueued (the state a recovery re-dispatch lands the run in — the engine's
                // ExecuteRunAsync claims Enqueued → Running), then re-run the engine to re-enter the node.
                await scope.Resolve<CodeSpaceDbContext>().WorkflowRun
                    .Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Enqueued));
            }
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId))
                    .ShouldBe(2, "NO double-spawn — the restart replayed the settled spawn decision, never re-staged its agents");

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "the run re-suspended on the same 2 agent waits (still pending)");

                (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending))
                    .ShouldBe(2, "exactly the original 2 agent waits remain — none leaked, none duplicated");

                // The spawn decision is still EXACTLY ONE ledger row (no second spawn claim).
                var rows = await Ledger(db, runId, teamId);
                rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).ShouldBe(1, "exactly one spawn decision row — the claim hop deduped the replay");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    // Resolve the run's pending SupervisorDecision self-advance wait via the SAME entry point the engine
    // enqueues post-commit (ResumeWaitAsync) — flips the run Pending so the next RunEngineAsync runs the next turn.
    private async Task ResolveSelfAdvanceAsync(Guid runId)
    {
        Guid waitId;
        using (var verify = _fixture.BeginScope())
        {
            waitId = (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision && w.Status == WorkflowWaitStatuses.Pending)).Id;
        }

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None);
    }

    private async Task<IReadOnlyList<string>> LedgerKinds(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        return (await Ledger(verify.Resolve<CodeSpaceDbContext>(), runId, teamId)).Select(r => r.DecisionKind).ToList();
    }

    private static async Task<IReadOnlyList<Core.Persistence.Entities.SupervisorDecisionRecord>> Ledger(CodeSpaceDbContext db, Guid runId, Guid teamId) =>
        await db.SupervisorDecisionRecord.AsNoTracking().Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderBy(d => d.Sequence).ToListAsync();

    // Drive the executor's terminal sequence (MarkRunning → Complete → Notify) without the sandboxed CLI —
    // the exact path AgentRunExecutor follows on a real completion (mirrors MapAgentResumeFlowTests).
    private async Task SimulateAgentCompletionAsync(Guid agentRunId, string summary)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var notifier = scope.Resolve<IAgentRunCompletionNotifier>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary }, CancellationToken.None);
        await notifier.NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-spawn-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(),
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

    // manual → sup (agent.supervisor) → terminal
    private static WorkflowDefinition SupervisorDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };
}
