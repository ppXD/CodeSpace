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
/// 🟢 THE PR-E E5 CROWN JEWEL (high fidelity — REAL engine + REAL <see cref="SupervisorTurnService"/> +
/// <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> + REAL
/// <see cref="AgentRunService"/> + REAL barrier over real Postgres; the scripted decider stands in for the LLM,
/// agent completion is simulated). Proves the bounds + governance are PRODUCTION-SAFE end-to-end:
/// <list type="bullet">
///   <item>TOTAL-SPAWN CAP: a decider that keeps spawning, under <c>maxTotalSpawns</c>, force-STOPs the run
///         CLEANLY (run terminal Success, terminal reason "total spawn cap reached") once a further spawn would
///         breach the cap — and the cap is NOT exceeded (exactly the allotted agents exist). Ledger-counted, so
///         the bound holds across the multi-turn barrier resume.</item>
///   <item>APPROVAL POLICY gate-before-spawn: with <c>approvalPolicy: "spawns"</c>, the turn-1 spawn routes
///         through the HITL gate — it is rewritten into an ask_human APPROVAL card + parks on an Action wait
///         BEFORE any agent run is created (zero <c>agent.run</c> rows).</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorBoundsFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;

    public SupervisorBoundsFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    // ── Total-spawn cap: keeps spawning → force-STOP at the cap (the cap not exceeded) ──

    [Fact]
    public async Task A_decider_that_keeps_spawning_force_stops_at_the_total_spawn_cap()
    {
        SetScript(s => s.PlanThenSpawnForever());

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        // maxTotalSpawns = 3, each spawn fans out 2 → turn 1 stages 2 (≤3 ok); turn 2's spawn of 2 → 4 > 3 → STOP.
        var workflowId = await CreateWorkflowAsync(teamId, userId, """{"goal":"ship it","maxTotalSpawns":3}""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            // Turn 0: plan → self-advance. Turn 1: spawn(2) → stages 2 real agents + parks on the barrier.
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            Guid[] agents;
            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                agents = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).Select(r => r.Id).ToArrayAsync();
                agents.Length.ShouldBe(2, "turn 1 spawned 2 agents — within the cap of 3");
            }

            // Both agents complete → the barrier resumes the supervisor → turn 2 wants to spawn 2 more (total 4 > 3).
            foreach (var agent in agents) await SimulateAgentCompletionAsync(agent);
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Success, "the total-spawn cap force-STOPS the run CLEANLY (a terminal stop, not a crash)");

                // The cap was NOT exceeded — still exactly the 2 agents from turn 1, no third spawn fired.
                (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId))
                    .ShouldBe(2, "the cap held — the over-cap spawn was refused, no extra agents created");

                var rows = await Ledger(db, runId, teamId);
                rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).ShouldBe(1, "exactly one spawn executed — the second was force-stopped before executing");

                var stop = rows.Single(r => r.DecisionKind == SupervisorDecisionKinds.Stop);
                stop.PayloadJson.ShouldContain(SupervisorStopReasons.TotalSpawnCapReached, customMessage: "the forced stop records the DISTINCT total-spawn-cap reason — check the supervisor_decision_record payload");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ── Approval policy: the spawn parks for a human BEFORE any agent is created ──────

    [Fact]
    public async Task An_approval_policy_parks_the_spawn_for_a_human_before_any_agent_is_created()
    {
        SetScript(s => s.PlanThenSpawnForever());

        var (teamId, userId, conversationId) = await SeedTeamWithConversationAsync();
        var config = $$"""{"goal":"ship it","approvalPolicy":"spawns","conversationId":"{{conversationId}}"}""";
        var workflowId = await CreateWorkflowAsync(teamId, userId, config);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            // Turn 0: plan → self-advance. Turn 1: the decider WANTS to spawn, but the approval policy rewrites it
            // into an ask_human approval card + parks — BEFORE any agent.run run is created.
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "the gated spawn parks for a human — it does NOT proceed");

                // The decisive assertion: NO agent run was created — the spawn was gated BEFORE its side effect.
                (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId))
                    .ShouldBe(0, "the approval gate held — zero agents created until a human approves");

                // It parked on a single ask_human Action wait (the reused E4 HITL park), and recorded an ask_human
                // decision (NOT a spawn) in the ledger.
                (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending && w.IterationKey.EndsWith("#ask")))
                    .ShouldBe(1, "the gated spawn parked on the approval ask_human Action wait");

                var rows = await Ledger(db, runId, teamId);
                rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).ShouldBe(0, "no spawn decision settled — the human gates it");
                rows.ShouldContain(r => r.DecisionKind == SupervisorDecisionKinds.AskHuman);
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ── Approve-then-proceed: the human approves, the re-emitted spawn EXECUTES (not re-gated) ──

    [Fact]
    public async Task An_approved_spawn_executes_rather_than_being_re_gated_into_another_ask_human()
    {
        SetScript(s => s.PlanThenSpawnForever());

        var (teamId, userId, conversationId) = await SeedTeamWithConversationAsync();
        var config = $$"""{"goal":"ship it","approvalPolicy":"spawns","conversationId":"{{conversationId}}"}""";
        var workflowId = await CreateWorkflowAsync(teamId, userId, config);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            // Turn 0 plan → self-advance. Turn 1 spawn → gated into an ask_human approval card + parks (zero agents).
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            string token;
            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                token = (await db.WorkflowRunWait.AsNoTracking()
                    .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending && w.IterationKey.EndsWith("#ask"))).Token;
                (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId)).ShouldBe(0, "still gated — no agent until the human approves");
            }

            // The human APPROVES via the real token-correlated resume path → turn 2's re-emitted spawn is bound to
            // the approval and EXECUTES (it is NOT re-gated into a second ask_human — the dead-end the fix removes).
            await ApproveAsync(token, userId, teamId);
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "the approved spawn ran + parked on its agent barrier — NOT another approval park");

                (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId))
                    .ShouldBe(2, "the human-approved spawn EXECUTED — 2 agents staged (the approve-then-proceed binding, not a re-gate dead-end)");

                // It parked on the spawn's AgentRun barrier (NOT a second ask_human approval Action wait).
                (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending))
                    .ShouldBe(2, "parked on the 2 agent waits the approved spawn staged");
                (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending && w.IterationKey.EndsWith("#ask")))
                    .ShouldBe(0, "no second approval card — the approval was bound to the re-emitted spawn");

                var rows = await Ledger(db, runId, teamId);
                rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).ShouldBe(1, customMessage: "exactly one spawn SETTLED — the approval let it proceed once, instead of looping ask→approve→ask forever");
                rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.AskHuman).ShouldBe(1, "exactly the ONE approval card — not a second one");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private void SetScript(Action<SupervisorDecisionScript> configure)
    {
        using var scope = _fixture.BeginScope();
        configure(scope.Resolve<SupervisorDecisionScript>());
    }

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

    private async Task ApproveAsync(string token, Guid actorUserId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IWorkflowResumeService>()
            .ResumeByActionTokenAsync(token, Core.Services.Supervisor.Executors.RealSupervisorActionExecutor.AnswerActionKey, actorUserId, SupervisorApprovalRequest.ApproveReply, values: null, teamId, CancellationToken.None);

        result.ShouldBe(ActionResumeResult.Resumed, "the human's approval resolves the supervisor's approval ask wait via the existing token-correlated resume path");
    }

    private async Task SimulateAgentCompletionAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var notifier = scope.Resolve<IAgentRunCompletionNotifier>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "done" }, CancellationToken.None);
        await notifier.NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<Core.Persistence.Entities.SupervisorDecisionRecord>> Ledger(CodeSpaceDbContext db, Guid runId, Guid teamId) =>
        await db.SupervisorDecisionRecord.AsNoTracking().Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderBy(d => d.Sequence).ToListAsync();

    private async Task<(Guid teamId, Guid userId, Guid conversationId)> SeedTeamWithConversationAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var slug = "sup-approve-" + Guid.NewGuid().ToString("N")[..8];
        var conversationId = await scope.Resolve<Core.Services.Chat.IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, userId, CancellationToken.None);

        return (teamId, userId, conversationId);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, string supervisorConfigJson)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-bounds-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(supervisorConfigJson),
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

    private static WorkflowDefinition SupervisorDefinition(string supervisorConfigJson) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(supervisorConfigJson), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };
}
