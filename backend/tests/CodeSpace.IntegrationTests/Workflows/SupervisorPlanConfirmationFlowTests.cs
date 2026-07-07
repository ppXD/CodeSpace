using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 High fidelity (REAL engine + REAL turn service + REAL executor card post + REAL Action-wait resume +
/// REAL WorkPlan store over real Postgres; the scripted decider stands in for the LLM). The S3
/// PLAN-CONFIRMATION GATE end-to-end — <c>requirePlanConfirmation</c> on the supervisor config:
/// <list type="bullet">
///   <item>APPROVE ARC: turn 0 plan → the GATE (not the decider) injects the confirmation ask_human → the run
///         parks Suspended with the WorkPlan flipped AwaitingConfirmation and NO agent created. The structured
///         confirm endpoint path (<see cref="IWorkPlanConfirmationService"/>) answers approve → the released
///         decider sees the folded answer → stop echoing it → Success, WorkPlan Confirmed.</item>
///   <item>FEEDBACK → REVISED-PLAN ARC: the answer is revision feedback → the decider authors plan v2 → the gate
///         RE-PARKS on a second card (v2 AwaitingConfirmation, v1 Rejected) — the deer-flow [EDIT_PLAN] loop on
///         the durable tape. The second answer arrives via the RAW conversation-card resume path, proving both
///         surfaces converge on the same wait.</item>
///   <item>GATE OFF: the same script completes with ZERO ask_human rows — byte-identical pre-S3 behaviour.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorPlanConfirmationFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;

    public SupervisorPlanConfirmationFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanConfirmReactive();
    }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task An_authored_plan_parks_for_confirmation_and_an_approve_releases_execution()
    {
        var (teamId, userId, conversationId) = await SeedTeamWithConversationAsync();
        var runId = await CreateSupervisorRunAsync(teamId, userId, conversationId, requireConfirmation: true);

        // ── Turn 0 plans (self-advance); turn 1 is the GATE's injection: confirmation card + park (the decider is never asked). ──
        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "an unconfirmed plan parks the run — no agent is created before the operator answers");

            var ask = (await Ledger(db, runId, teamId)).Single(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman);
            var question = SupervisorOutcome.ReadAskHumanQuestion(ask.OutcomeJson)!;
            question.ShouldContain(SupervisorPlanConfirmation.ConfirmationMarker, customMessage: "the card is the GATE's own (marker-carrying), not a decider content ask");
            question.ShouldContain("plan v1");
            question.ShouldContain("2 step(s)");

            var plan = await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == runId);
            plan.Status.ShouldBe(WorkPlanStatuses.AwaitingConfirmation, "the persisted contract mirrors the park — the checklist renders the confirm card off this status");

            (await db.AgentRun.AsNoTracking().CountAsync(a => a.TeamId == teamId)).ShouldBe(0, "fail-closed: zero agents staged before confirmation");
        }

        // ── The operator approves through the STRUCTURED endpoint path (checklist confirm card → service). ──
        var outcome = await ConfirmAsync(runId, teamId, userId, approve: true, feedback: null);
        outcome.ShouldNotBeNull();
        outcome.Resumed.ShouldBeTrue("the answer resolved the park and re-dispatched the run");

        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "approve released the gate → the decider stopped");

            var stop = (await Ledger(db, runId, teamId)).Single(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
            JsonDocument.Parse(stop.OutcomeJson!).RootElement.GetProperty("summary").GetString()
                .ShouldBe("confirmed: approve", "the released decider saw the folded confirmation answer in its context");

            (await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == runId)).Status
                .ShouldBe(WorkPlanStatuses.Confirmed, "the approve settled the plan's confirmation lifecycle");

            (await Ledger(db, runId, teamId)).Select(d => d.DecisionKind).ShouldBe(
                new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.AskHuman, SupervisorDecisionKinds.Stop },
                customMessage: "the legible tape: plan → the gate's card → the released stop");
        }

        // A second confirm finds nothing pending — the card is first-answer-wins and already settled.
        (await ConfirmAsync(runId, teamId, userId, approve: true, feedback: null))
            .ShouldBeNull("no pending confirmation remains after the answer");
    }

    [Fact]
    public async Task Revision_feedback_yields_a_revised_plan_version_that_regates_then_an_approve_ships_it()
    {
        var (teamId, userId, conversationId) = await SeedTeamWithConversationAsync();
        var runId = await CreateSupervisorRunAsync(teamId, userId, conversationId, requireConfirmation: true);

        // Park on v1's confirmation.
        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);

        // ── Request changes through the endpoint path — the answer becomes the decider's revision brief. ──
        var outcome = await ConfirmAsync(runId, teamId, userId, approve: false, feedback: "merge the steps into one");
        outcome!.Resumed.ShouldBeTrue();

        // The released decider authors plan v2 (self-advance); the gate injects a SECOND card and re-parks.
        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);

        string secondToken;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the REVISED plan re-gates — every authored version needs its own confirmation");

            var plans = await db.WorkPlan.AsNoTracking().Where(p => p.WorkflowRunId == runId).OrderBy(p => p.Version).ToListAsync();
            plans.Count.ShouldBe(2, "the revision is a NEW immutable version, never an edit of v1");
            plans[0].Status.ShouldBe(WorkPlanStatuses.Rejected, "v1 settled as rejected when the feedback arrived");
            plans[1].Status.ShouldBe(WorkPlanStatuses.AwaitingConfirmation);
            plans[1].ItemsJson.ShouldContain(ScriptedSupervisorDecider.SubtaskRevised, customMessage: "v2 carries the revised (merged) subtask");
            plans[1].ItemsJson.ShouldContain("merge the steps into one", customMessage: "the revision brief reached the revised contract");

            var asks = (await Ledger(db, runId, teamId)).Where(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman).ToList();
            asks.Count.ShouldBe(2, "one card per authored version");
            SupervisorOutcome.ReadAskHumanQuestion(asks[1].OutcomeJson)!.ShouldContain("plan v2");
            SupervisorOutcome.ReadAskHumanQuestion(asks[1].OutcomeJson)!.ShouldContain("1 step(s)");

            secondToken = SupervisorOutcome.ReadHumanWaitToken(asks[1].OutcomeJson)!;
        }

        // ── Approve v2 via the RAW conversation-card resume (the card's own Answer button path) — both surfaces
        //    converge on the SAME wait, so the checklist endpoint and the chat card can never race two answers. ──
        using (var scope = _fixture.BeginScope())
        {
            var resumed = await scope.Resolve<IWorkflowResumeService>()
                .ResumeByActionTokenAsync(secondToken, RealSupervisorActionExecutor.AnswerActionKey, userId, "approve", values: null, teamId, CancellationToken.None);
            resumed.ShouldBe(ActionResumeResult.Resumed);
        }

        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var plans = await db.WorkPlan.AsNoTracking().Where(p => p.WorkflowRunId == runId).OrderBy(p => p.Version).ToListAsync();
            plans.Select(p => p.Status).ShouldBe(new[] { WorkPlanStatuses.Rejected, WorkPlanStatuses.Confirmed },
                customMessage: "the version ladder is the audit trail: v1 rejected with feedback, v2 confirmed and shipped");

            (await Ledger(db, runId, teamId)).Select(d => d.DecisionKind).ShouldBe(
                new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.AskHuman, SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.AskHuman, SupervisorDecisionKinds.Stop },
                customMessage: "the full edit loop on one tape: plan → card → revised plan → card → stop");
        }
    }

    [Fact]
    public async Task Without_the_flag_the_same_script_completes_with_no_confirmation_card()
    {
        var (teamId, userId, conversationId) = await SeedTeamWithConversationAsync();
        var runId = await CreateSupervisorRunAsync(teamId, userId, conversationId, requireConfirmation: false);

        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "no gate — the run flows plan → stop without parking");

        (await Ledger(db, runId, teamId)).Select(d => d.DecisionKind).ShouldBe(
            new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Stop },
            customMessage: "byte-identical pre-S3 tape: zero ask_human rows");

        (await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == runId)).Status
            .ShouldBe(WorkPlanStatuses.Authored, "the contract persists but its confirmation lifecycle never starts");

        (await ConfirmAsync(runId, teamId, userId, approve: true, feedback: null))
            .ShouldBeNull("the confirm endpoint conflates 'gate off' with 'nothing pending' — no existence leak");
    }

    [Fact]
    public async Task A_run_with_no_conversation_surface_stops_fail_closed_instead_of_spawning_unconfirmed()
    {
        // The launch path wires no conversation today — the gate must STOP the run with a legible reason, never
        // let the card degrade to a no-surface self-advance that spawns the plan unconfirmed.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await CreateSupervisorRunWithoutConversationAsync(teamId, userId);

        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "the force-stop is a clean terminal, not a hang");

        var ledger = await Ledger(db, runId, teamId);
        ledger.Select(d => d.DecisionKind).ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Stop },
            customMessage: "no ask_human was ever recorded — the gate stopped before any degraded card");
        ledger[1].PayloadJson.ShouldContain(SupervisorStopReasons.PlanConfirmationUnavailable);

        (await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == runId)).Status
            .ShouldBe(WorkPlanStatuses.Authored, "no confirmation was ever requested — the status never lies");

        (await db.AgentRun.AsNoTracking().CountAsync(a => a.TeamId == teamId)).ShouldBe(0, "fail-closed: zero agents");
    }

    private async Task<Guid> CreateSupervisorRunWithoutConversationAsync(Guid teamId, Guid userId)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "sup-confirm-nosurface-" + Guid.NewGuid().ToString("N")[..6],
                Description = null,
                Definition = new WorkflowDefinition
                {
                    SchemaVersion = 1,
                    Nodes = new List<NodeDefinition>
                    {
                        new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                        new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature","requirePlanConfirmation":true}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                        new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    },
                    Edges = new List<EdgeDefinition> { new() { From = "start", To = "sup" }, new() { From = "sup", To = "end" } },
                },
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    [Fact]
    public async Task A_launched_deep_task_with_the_gate_parks_on_a_real_card_in_the_sessions_channel_then_approve_ships()
    {
        // THE product path the S3 review flagged as untested: a real LAUNCH (not a hand-authored definition) with
        // requirePlanConfirmation — S4a's auto-staged session channel gives the card a surface, the run parks, the
        // checklist confirm endpoint releases it. Launch → plan → park-with-card → approve → stop, end to end.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();

        LaunchTaskResult launched;
        using (var scope = _fixture.BeginScope())
        {
            launched = await scope.Resolve<ITaskLaunchService>().LaunchAsync(new TaskLaunchRequest
            {
                TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
                TaskText = "Ship the whole feature end to end", RequestedEffort = TaskEffortModes.Deep,
                RequirePlanConfirmation = true,
            }, CancellationToken.None);
        }

        launched.Route.ProjectionKind.ShouldBe(TaskProjectionKinds.Supervisor);

        // Drain the dispatch + walk + self-advance job chain until the run parks on the confirmation.
        await jobClient.WaitForPendingAsync();

        Guid conversationId;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == launched.RunId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the launched run parked on the plan confirmation — zero agents before the operator answers");

            conversationId = (await db.WorkSession.AsNoTracking().SingleAsync(x => x.Id == launched.SessionId)).ConversationId!.Value;

            var card = await db.Message.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(m => m.ConversationId == conversationId && m.InteractionJson != null && m.DeletedDate == null);
            card.Body.ShouldContain(SupervisorPlanConfirmation.ConfirmationMarker, customMessage: "the REAL confirmation card landed in the auto-staged session channel");

            (await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == launched.RunId)).Status
                .ShouldBe(WorkPlanStatuses.AwaitingConfirmation);

            (await db.AgentRun.AsNoTracking().CountAsync(a => a.TeamId == teamId)).ShouldBe(0);
        }

        var outcome = await ConfirmAsync(launched.RunId, teamId, userId, approve: true, feedback: null);
        outcome!.Resumed.ShouldBeTrue();

        await jobClient.WaitForPendingAsync();

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == launched.RunId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "approve released the launched run end to end");

            (await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == launched.RunId)).Status
                .ShouldBe(WorkPlanStatuses.Confirmed);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private async Task<WorkPlanConfirmationOutcome?> ConfirmAsync(Guid runId, Guid teamId, Guid userId, bool approve, string? feedback)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkPlanConfirmationService>().AnswerAsync(runId, teamId, userId, approve, feedback, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<Core.Persistence.Entities.SupervisorDecisionRecord>> Ledger(CodeSpaceDbContext db, Guid runId, Guid teamId) =>
        await db.SupervisorDecisionRecord.AsNoTracking().Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderBy(d => d.Sequence).ToListAsync();

    private async Task<(Guid TeamId, Guid UserId, Guid ConversationId)> SeedTeamWithConversationAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var slug = "sup-confirm-" + Guid.NewGuid().ToString("N")[..8];
        var conversationId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, userId, CancellationToken.None);

        return (teamId, userId, conversationId);
    }

    private async Task<Guid> CreateSupervisorRunAsync(Guid teamId, Guid userId, Guid conversationId, bool requireConfirmation)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId, conversationId, requireConfirmation);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, Guid conversationId, bool requireConfirmation)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-confirm-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(conversationId, requireConfirmation),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>Resolve a plan turn's synchronous self-advance wait (SupervisorDecision) so the next engine drive runs the following turn — the test-side stand-in for the post-commit re-dispatch job.</summary>
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

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    // manual → sup (agent.supervisor, with the confirm gate + a conversation for the card) → terminal
    private static WorkflowDefinition SupervisorDefinition(Guid conversationId, bool requireConfirmation) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json($$"""{"goal":"ship the feature","conversationId":"{{conversationId}}"{{(requireConfirmation ? ",\"requirePlanConfirmation\":true" : "")}}}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };
}
