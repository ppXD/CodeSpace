using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/> turn loop with a critic-decorated
/// decider): the P1d "checks before critics" ordering, proven end-to-end. A structurally-invalid PLAN (a cyclic
/// <c>DependsOn</c>) is force-stopped by the free Tier-0 <see cref="SupervisorPlanValidator"/> — and the model critic
/// is NEVER billed for it, because the decorator runs the deterministic check first and short-circuits. The
/// complement guards the boundary: a WELL-FORMED plan under the SAME critic mode IS reviewed exactly as before, so
/// the pre-filter only silences a doomed plan, never valid work. The decorator's skip logic is pinned in
/// <c>CriticSupervisorDeciderDecoratorTests</c>; this proves it holds over the real loop where the tokens are spent.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorChecksBeforeCriticFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorChecksBeforeCriticFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    [Fact]
    public async Task An_invalid_plan_force_stops_without_ever_billing_the_model_critic()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var critic = new RecordingCritic();

        // a→b→a is an unsatisfiable cycle the validator rejects; the critic is in Gate mode, so absent P1d it would review.
        await RunPlanTurnAsync(runId, teamId, critic, ("a", new[] { "b" }), ("b", new[] { "a" }));

        var decisions = await LedgerAsync(runId, teamId);

        var stop = decisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
        stop.PayloadJson.ShouldContain(SupervisorStopReasons.PlanInvalid, customMessage: "the free validator force-stops the doomed plan");

        critic.Requests.ShouldBe(0, "P1d: the deterministic check runs FIRST — a plan the gate will reject anyway never bills a model call");
    }

    [Fact]
    public async Task A_well_formed_plan_under_the_same_critic_mode_is_still_reviewed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var critic = new RecordingCritic();

        await RunPlanTurnAsync(runId, teamId, critic, ("a", null), ("b", new[] { "a" }));

        var decisions = await LedgerAsync(runId, teamId);

        decisions.ShouldContain(d => d.DecisionKind == SupervisorDecisionKinds.Plan, "a well-formed DAG passes the validator and is recorded");
        critic.Requests.ShouldBe(1, "the pre-filter never fires on valid work — the critic reviews it exactly as before");
    }

    // ─── Helpers ───

    private async Task RunPlanTurnAsync(Guid runId, Guid teamId, RecordingCritic critic, params (string Id, string[]? DependsOn)[] subtasks)
    {
        using var scope = _fixture.BeginScope();

        var decider = new CriticSupervisorDeciderDecorator(new PlanDecider(subtasks), critic, new NoAgentPlanReviewer());

        var service = new SupervisorTurnService(
            scope.Resolve<ISupervisorDecisionLog>(),
            decider,
            scope.Resolve<ISupervisorActionExecutor>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<ISupervisorAcceptanceGrader>(),
            scope.Resolve<IDecisionQueueService>(),
            scope.Resolve<IDecisionArbiter>(),
            scope.Resolve<IDecisionAnswerService>(),
            scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<ILogger<SupervisorTurnService>>());

        var goalConfig = new SupervisorGoalConfig { Goal = Goal, DecisionReviewMode = ReviewMode.Gate };

        await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, goalConfig, CancellationToken.None);
    }

    private async Task<IReadOnlyList<Core.Persistence.Entities.SupervisorDecisionRecord>> LedgerAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .ToListAsync();
    }

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
            Name = "sup-checks-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>Counts every review request + approves whatever it sees — so a NON-ZERO count is the only signal that P1d let the critic run.</summary>
    private sealed class RecordingCritic : IStructuredCritic
    {
        public int Requests { get; private set; }

        public Task<CriticVerdict> ReviewAsync(CriticRequest request, Guid teamId, Guid? reviewerModelId, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "recorded + approved" });
        }
    }

    /// <summary>No grounded plan review — the ladder falls straight through to the model critic (which P1d may or may not reach).</summary>
    private sealed class NoAgentPlanReviewer : IAgentPlanReviewer
    {
        public Task<CriticVerdict> ReviewAsync(PlanReviewRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CriticVerdict.ReviewFailed(ReviewMode.Gate, "agent-reviewer: not requested"));
    }

    /// <summary>A decider that emits a single PLAN with the given subtasks + DependsOn edges — drives the validator over the real turn loop.</summary>
    private sealed class PlanDecider : ISupervisorDecider
    {
        private readonly (string Id, string[]? DependsOn)[] _subtasks;

        public PlanDecider((string Id, string[]? DependsOn)[] subtasks) => _subtasks = subtasks;

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new
            {
                goal = Goal,
                subtasks = _subtasks.Select(s => s.DependsOn is null
                    ? (object)new { id = s.Id, title = s.Id, instruction = "do " + s.Id }
                    : new { id = s.Id, title = s.Id, instruction = "do " + s.Id, dependsOn = s.DependsOn }),
            }, AgentJson.Options);

            return Task.FromResult(new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = payload });
        }
    }
}
