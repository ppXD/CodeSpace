using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): DC-1 (Universal Delivery Contract) — a fresh plan's model-PROPOSED delivery
/// contract is server-clamped against the OPERATOR's own pre-declared <see cref="SupervisorGoalConfig.DeliverySpec"/>
/// (operator wins per field) BEFORE the decision is claimed + frozen, driven through the REAL <see cref="SupervisorTurnService"/>
/// against real Postgres — proving the clamp actually fires inside the turn pipeline, not just as a pure function
/// in isolation. Also proves the plan-confirmation card (S3) names any side-effecting delivery behaviour before
/// the operator approves.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorPlanDeliveryFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    private readonly PostgresFixture _fixture;

    public SupervisorPlanDeliveryFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_operators_declared_false_overrides_the_models_proposed_true_in_the_persisted_plan()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var goalConfig = new SupervisorGoalConfig { Goal = Goal, DeliverySpec = new DeliverySpec { OpenPullRequest = false } };
        var decider = new AlwaysPlanDecider(new DeliverySpec { OpenPullRequest = true, TargetBranch = "develop" });

        await RunTurnAsync(runId, teamId, decider, goalConfig);

        var persisted = await LatestPlanPayloadAsync(runId, teamId);
        var delivery = SupervisorOutcome.ReadPlanDelivery(persisted);

        delivery.ShouldNotBeNull();
        delivery!.OpenPullRequest.ShouldBe(false, "the operator's explicit false overrides the model's proposed true, all the way through the real turn pipeline");
    }

    [Fact]
    public async Task No_operator_contract_persists_the_models_own_proposal_untouched()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var decider = new AlwaysPlanDecider(new DeliverySpec { OpenPullRequest = true, TargetBranch = "main" });

        await RunTurnAsync(runId, teamId, decider, goalConfig: new SupervisorGoalConfig { Goal = Goal });

        var delivery = SupervisorOutcome.ReadPlanDelivery(await LatestPlanPayloadAsync(runId, teamId));

        delivery!.OpenPullRequest.ShouldBe(true);
        delivery.TargetBranch.ShouldBe("main");
    }

    [Fact]
    public async Task The_plan_confirmation_card_names_the_auto_opened_pull_request_before_approval()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var conversationId = Guid.NewGuid();

        var goalConfig = new SupervisorGoalConfig { Goal = Goal, RequirePlanConfirmation = true, DeliverySpec = new DeliverySpec { OpenPullRequest = true, TargetBranch = "main" } };
        var decider = new AlwaysPlanDecider(delivery: null);

        // Turn 1: no prior plan exists yet, so the gate has nothing to confirm — the decider authors the FIRST
        // plan (clamped with the operator's contract). Turn 2: the tape now shows an unconfirmed plan, so the
        // gate injects the confirmation card BEFORE the decider ever runs again. A FRESH scope + service per
        // turn, mirroring every other turn-driver in this suite, so the tape is re-rehydrated from Postgres
        // exactly like production (never a stale in-memory DbContext carried across turns).
        using (var scope = _fixture.BeginScope())
            await NewTurnService(scope, decider).RunTurnAsync(runId, teamId, NodeId, Goal, conversationId, goalConfig, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            await NewTurnService(scope, decider).RunTurnAsync(runId, teamId, NodeId, Goal, conversationId, goalConfig, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var latest = await verify.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderByDescending(d => d.Sequence).FirstAsync();

        latest.DecisionKind.ShouldBe(SupervisorDecisionKinds.AskHuman, "an unconfirmed plan parks for the operator before any agent runs");

        var question = JsonDocument.Parse(latest.PayloadJson).RootElement.GetProperty("question").GetString()!;
        question.ShouldContain("automatically open a pull request against main", customMessage: "the operator must see the side-effecting delivery behaviour BEFORE approving the plan");
    }

    [Fact]
    public async Task A_replan_that_drops_an_already_approved_pull_request_flags_the_revocation_in_its_own_card()
    {
        // DC-2a downgrade-delta: NO operator declaration is in play (an operator's own true is permanently sticky
        // per SupervisorDeliveryClamp, so a downgrade can only meaningfully originate from the MODEL changing its
        // own mind between plan versions). v1 proposes + gets approved; v2 proposes nothing — its OWN confirmation
        // card must flag that the already-approved PR is being revoked, not silently drop it.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var conversationId = Guid.NewGuid();
        var goalConfig = new SupervisorGoalConfig { Goal = Goal, RequirePlanConfirmation = true };

        var proposesPr = new AlwaysPlanDecider(new DeliverySpec { OpenPullRequest = true, TargetBranch = "main" });
        var proposesNothing = new AlwaysPlanDecider(delivery: null);

        await RunTurnAsync(runId, teamId, proposesPr, goalConfig, conversationId);              // turn 1: plan v1 (proposes true)
        await RunTurnAsync(runId, teamId, proposesPr, goalConfig, conversationId);              // turn 2: the gate injects v1's card
        await ApproveLatestConfirmationCardAsync(runId, teamId);
        await RunTurnAsync(runId, teamId, proposesNothing, goalConfig, conversationId);         // turn 3: release v1 → decider authors v2 (proposes nothing)
        await RunTurnAsync(runId, teamId, proposesNothing, goalConfig, conversationId);         // turn 4: the gate injects v2's OWN card

        var latestAsk = await LatestAskHumanAsync(runId, teamId);
        var question = SupervisorOutcome.ReadAskHumanQuestion(latestAsk.OutcomeJson)!;
        question.ShouldContain("plan v2");
        question.ShouldContain("REVOKES", customMessage: "v2 silently drops v1's already-approved PR — the card must flag it before the operator approves the weaker plan");
    }

    // ─── Drive a real turn ─────────────────────────────────────────────────────────

    private async Task RunTurnAsync(Guid runId, Guid teamId, ISupervisorDecider decider, SupervisorGoalConfig goalConfig, Guid? conversationId = null)
    {
        using var scope = _fixture.BeginScope();
        var service = NewTurnService(scope, decider);
        await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId, goalConfig, CancellationToken.None);
    }

    /// <summary>
    /// 🟡 Medium-mock (Rule 12): directly folds an "approve" answer onto the tape's latest ask_human decision —
    /// the test-side stand-in for the operator's checklist click, at the SAME abstraction level as this file's
    /// OWN direct <c>RunTurnAsync</c> turn-driving (every test here bypasses the engine + real wait/resume; this
    /// helper is consistent with that, not a new degradation). Deliberately skips <c>WorkflowRunWait</c> /
    /// <c>IWorkflowResumeService</c> entirely — that plumbing (resolve-by-token → <c>FoldAskHumanAnswer</c>) is
    /// covered end-to-end elsewhere (<c>SupervisorPlanConfirmationFlowTests</c>'s real-engine approve/re-plan
    /// arcs); this test isolates <see cref="SupervisorPlanConfirmation.LastApprovedDelivery"/> + the redesigned
    /// <c>DeliverySummary</c> rendering, which need only a correctly-shaped tape, not the wait machinery that
    /// produces it. Safe against <c>FoldAskHumanAnswer</c>'s own rehydrate re-fold: it is a no-op pass-through
    /// when the card's token has no matching RESOLVED <c>WorkflowRunWait</c> row (there is none here), so the
    /// answer written below is never clobbered on the next <c>RunTurnAsync</c>.
    /// </summary>
    private async Task ApproveLatestConfirmationCardAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var latest = await LatestAskHumanRowAsync(db, runId, teamId);

        latest.OutcomeJson = SupervisorOutcome.FoldAnswerOnto(latest.OutcomeJson, "approve");
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<Core.Persistence.Entities.SupervisorDecisionRecord> LatestAskHumanAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await LatestAskHumanRowAsync(scope.Resolve<CodeSpaceDbContext>(), runId, teamId);
    }

    private static async Task<Core.Persistence.Entities.SupervisorDecisionRecord> LatestAskHumanRowAsync(CodeSpaceDbContext db, Guid runId, Guid teamId) =>
        await db.SupervisorDecisionRecord
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.AskHuman)
            .OrderByDescending(d => d.Sequence).FirstAsync();

    private async Task<string> LatestPlanPayloadAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var row = await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Plan)
            .OrderByDescending(d => d.Sequence).FirstAsync();

        return row.PayloadJson;
    }

    /// <summary>The REAL <see cref="SupervisorTurnService"/>, every dependency resolved from the scope except the decider (scripted per test) — mirrors <c>SupervisorPublishGateFlowTests.NewTurnService</c>.</summary>
    private static SupervisorTurnService NewTurnService(ILifetimeScope scope, ISupervisorDecider decider) => new(
        scope.Resolve<ISupervisorDecisionLog>(),
        decider,
        scope.Resolve<ISupervisorActionExecutor>(),
        scope.Resolve<CodeSpaceDbContext>(),
        scope.Resolve<ISupervisorAcceptanceGrader>(),
        scope.Resolve<Core.Services.Decisions.IDecisionQueueService>(),
        scope.Resolve<Core.Services.Supervisor.Arbiter.IDecisionArbiter>(),
        scope.Resolve<Core.Services.Decisions.IDecisionAnswerService>(),
        scope.Resolve<Core.Services.Plans.IWorkPlanService>(),
        scope.Resolve<Core.Services.Workflows.Lifecycle.IRunRecordLogger>(),
        scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
        scope.Resolve<Core.Services.Agents.Publish.IPublishManifestStore>(),
        scope.Resolve<Core.Services.Supervisor.ISupervisorPublishedBranchResolver>(),
        scope.Resolve<ILogger<SupervisorTurnService>>());

    /// <summary>A decider that always authors a plan with one subtask, proposing the given delivery contract (or none).</summary>
    private sealed class AlwaysPlanDecider : ISupervisorDecider
    {
        private readonly DeliverySpec? _delivery;

        public AlwaysPlanDecider(DeliverySpec? delivery) => _delivery = delivery;

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Plan,
                PayloadJson = JsonSerializer.Serialize(new SupervisorPlanPayload
                {
                    Goal = Goal,
                    Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "T", Instruction = "do it" } },
                    Delivery = _delivery,
                }, AgentJson.Options),
            });
    }

    // ─── Seeding ────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-plan-delivery-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json($$"""{"goal":"{{Goal}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition> { new() { From = "start", To = NodeId }, new() { From = NodeId, To = "end" } },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}
