using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The LOOP-TIER work-plan producer (triad S1) through the REAL engine + REAL <c>SupervisorTurnService</c> +
/// <c>RealSupervisorActionExecutor</c> over real Postgres: a supervisor <c>plan</c> decision persists the run's
/// durable <c>work_plan</c> version (the SAME producer-agnostic artifact the <c>plan.author</c> node writes),
/// and a restart re-entry replays the settled decision WITHOUT a duplicate version (the per-turn origin key).
/// The scripted decider stands in for the LLM (default plan→stop arc); everything else is production.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorWorkPlanFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;

    public SupervisorWorkPlanFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task A_supervisor_plan_decision_persists_the_runs_work_plan_version()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Turn 0: plan (synchronous, self-advances) → resolve the self-advance → turn 1: stop → Success.
        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var plan = await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == runId);
        plan.TeamId.ShouldBe(teamId);
        plan.Version.ShouldBe(1);
        plan.OriginKind.ShouldBe(WorkPlanOrigins.Supervisor);
        plan.OriginKey.ShouldBe("boss#turn0", "the per-turn exactly-once key <nodeId>#turn{N} — the node id is deliberately NOT 'sup' so a regressed (empty) NodeId falling back to the literal 'sup' cannot silently pass");
        plan.Goal.ShouldBe("ship the feature", "the scripted plan restates the run goal");

        var items = JsonDocument.Parse(plan.ItemsJson).RootElement;
        items.GetArrayLength().ShouldBe(2, "the scripted plan authors two subtasks");
        items[0].GetProperty("id").GetString().ShouldBe(ScriptedSupervisorDecider.SubtaskA);
        items[0].GetProperty("title").GetString().ShouldBe("Alpha");
        items[0].GetProperty("instruction").GetString().ShouldBe("do alpha");
        items[1].GetProperty("id").GetString().ShouldBe(ScriptedSupervisorDecider.SubtaskB);
    }

    [Fact]
    public async Task A_restart_re_entry_replays_the_settled_plan_without_a_duplicate_version()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Drive turn 0 (plan) to settle — the run parks on the self-advance wait.
        await RunEngineAsync(runId);

        // SIMULATE A RESTART: re-dispatch the Suspended run (the crash-recovery shape the sibling supervisor
        // tests use). The node re-enters and the rehydrate REPLAYS the settled (terminal) plan decision — the
        // executor never re-runs it, so no second write can happen on THIS path. (The narrower claimed-Running
        // re-execution window — where the executor DOES re-run — is pinned by SupervisorPlanFoldFlowTests'
        // double-ExecuteAsync against the same origin key.)
        using (var scope = _fixture.BeginScope())
        {
            await scope.Resolve<CodeSpaceDbContext>().WorkflowRun
                .Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Enqueued));
        }
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkPlan.AsNoTracking().CountAsync(p => p.WorkflowRunId == runId))
            .ShouldBe(1, "the restart re-entry must NOT duplicate the plan version — one plan decision, one work_plan row");

        // Prove the re-entry actually happened (not a short-circuited no-op): the node re-ran, REPLAYED the
        // settled plan as its turn-0 context, and advanced to turn 1 = the scripted stop — the run reached
        // Success. If the engine ever skipped Enqueued runs outright, the run would still be Suspended here
        // and the count assert above would be vacuous.
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "the re-entry replayed the settled plan and advanced through the stop turn");
    }

    [Fact]
    public async Task The_checklist_tracks_items_from_in_progress_to_completed_across_a_spawn_arc()
    {
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnStop();   // plan → spawn(both) → stop

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // the binary-less harness must not run; completion is simulated below

        try
        {
            // Turn 0 (plan) → self-advance → turn 1 (spawn stages 2 agents + parks on the barrier).
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            Guid agent0, agent1;
            using (var mid = _fixture.BeginScope())
            {
                var db = mid.Resolve<CodeSpaceDbContext>();
                var waits = await db.WorkflowRunWait.AsNoTracking()
                    .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
                    .OrderBy(w => w.IterationKey).ToListAsync();
                waits.Count.ShouldBe(2);
                agent0 = Guid.Parse(waits[0].Token);
                agent1 = Guid.Parse(waits[1].Token);

                // MID-FLIGHT: the staged (unfolded) wave reads the LIVE agent statuses → both items InProgress.
                var inFlight = await mid.Resolve<IWorkPlanChecklistService>().GetCurrentAsync(runId, teamId, CancellationToken.None);
                inFlight.ShouldNotBeNull();
                inFlight!.Items.Count.ShouldBe(2);
                inFlight.Items.ShouldAllBe(i => i.State == WorkPlanItemStates.InProgress && i.Attempts == 1 && i.AgentRunId != null);
            }

            // Both agents complete → the barrier resumes → turn 2 stop → run Success.
            await SimulateAgentCompletionAsync(agent0, "ALPHA-DONE");
            await SimulateAgentCompletionAsync(agent1, "BETA-DONE");
            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

            // COMPLETED: the folded results drive the states — both items ticked, latest agent linked, one attempt each.
            var done = await verify.Resolve<IWorkPlanChecklistService>().GetCurrentAsync(runId, teamId, CancellationToken.None);
            done!.Items.ShouldAllBe(i => i.State == WorkPlanItemStates.Completed && i.Attempts == 1);
            done.Items.Select(i => i.Item.Id).ShouldBe(new[] { ScriptedSupervisorDecider.SubtaskA, ScriptedSupervisorDecider.SubtaskB });

            // Pin the POSITIONAL join exactly (no ignoreOrder): wait keys #turn1#0/#turn1#1 make agent0 the
            // sa slot and agent1 the sb slot — a transposed join is the projection's worst defect.
            done.Items[0].AgentRunId.ShouldBe(agent0, "sa must link the k=0 agent");
            done.Items[1].AgentRunId.ShouldBe(agent1, "sb must link the k=1 agent");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ─── Helpers (mirror SupervisorSpawnFlowTests) ───────────────────────────

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

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-workplan-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "boss", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition>
                {
                    new() { From = "start", To = "boss" },
                    new() { From = "boss", To = "end" },
                },
            },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    // Drive the executor's terminal sequence (MarkRunning → Complete → Notify) without the sandboxed CLI —
    // the exact path AgentRunExecutor follows on a real completion (mirrors SupervisorSpawnFlowTests).
    private async Task SimulateAgentCompletionAsync(Guid agentRunId, string summary)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var notifier = scope.Resolve<IAgentRunCompletionNotifier>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary }, CancellationToken.None);
        await notifier.NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }
}
