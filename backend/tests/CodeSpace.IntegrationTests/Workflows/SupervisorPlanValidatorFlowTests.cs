using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/> turn loop): the Tier-0 plan validator
/// wired end-to-end. A model that authors a CYCLIC <c>DependsOn</c> plan force-STOPs cleanly at plan time with the
/// distinct <see cref="SupervisorStopReasons.PlanInvalid"/> reason — never recording the bad plan, never spinning into a
/// no-progress stall — while a WELL-FORMED plan proceeds (it records a plan decision). The validator's decision logic is
/// pinned exhaustively in <c>SupervisorPlanValidatorTests</c>; this proves it actually gates the turn loop.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorPlanValidatorFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorPlanValidatorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    [Fact]
    public async Task A_cyclic_plan_force_stops_with_the_plan_invalid_reason_and_never_records_the_plan()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        // a depends on b, b depends on a — an unsatisfiable cycle the gate could never advance.
        await RunPlanTurnAsync(runId, teamId, ("a", new[] { "b" }), ("b", new[] { "a" }));

        var decisions = await LedgerAsync(runId, teamId);

        decisions.ShouldNotContain(d => d.DecisionKind == SupervisorDecisionKinds.Plan, "the structurally invalid plan is never recorded — it is rejected before the claim");

        var stop = decisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
        stop.PayloadJson.ShouldContain(SupervisorStopReasons.PlanInvalid, customMessage: "the forced stop records the distinct plan-invalid reason");
    }

    [Fact]
    public async Task A_well_formed_plan_proceeds_and_is_recorded()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await RunPlanTurnAsync(runId, teamId, ("a", null), ("b", new[] { "a" }));

        var decisions = await LedgerAsync(runId, teamId);

        decisions.ShouldContain(d => d.DecisionKind == SupervisorDecisionKinds.Plan, "a well-formed DAG passes the validator and the plan is recorded");
        decisions.ShouldNotContain(d => d.DecisionKind == SupervisorDecisionKinds.Stop, "a valid plan is not force-stopped");
    }

    // ─── Helpers ───

    private async Task RunPlanTurnAsync(Guid runId, Guid teamId, params (string Id, string[]? DependsOn)[] subtasks)
    {
        using var scope = _fixture.BeginScope();
        var service = new SupervisorTurnService(
            scope.Resolve<ISupervisorDecisionLog>(),
            new PlanDecider(subtasks),
            scope.Resolve<ISupervisorActionExecutor>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<ISupervisorAcceptanceGrader>(),
            scope.Resolve<IDecisionQueueService>(),
            scope.Resolve<IDecisionArbiter>(),
            scope.Resolve<IDecisionAnswerService>(),
            scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<ILogger<SupervisorTurnService>>());

        await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, goalConfig: null, CancellationToken.None);
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
            Name = "sup-planval-" + Guid.NewGuid().ToString("N")[..6],
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
