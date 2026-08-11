using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (the REAL executor from DI + real Postgres waits): the vacuous-Success fix — a GATE card (I3
/// publish / DC-2b delivery) in a run with NO usable conversation surface PARKS the run on its durable Action wait
/// instead of degrading into a self-advancing null answer. Before the fix, that degrade loop burned the
/// no-progress bound and terminalized accepted-unpublishable work as clean Success (run 31250002048: ask×9 →
/// forced stop → Success, zero delivery). A model's CONTENT ask keeps degrading — hanging an unattended run on a
/// clarifying question would be worse than proceeding; the pin covers both directions so neither can silently
/// swallow the other.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorGateCardParkFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorGateCardParkFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("I3 publish gate: the run has accepted work that could not be published — a human must resolve this")]
    [InlineData("Delivery gate: the contract wants a pull request — approve opening it")]
    public async Task A_gate_card_with_no_surface_parks_the_run_on_its_wait(string question)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        using var scope = _fixture.BeginScope();

        var execution = await scope.Resolve<ISupervisorActionExecutor>()
            .ExecuteAsync(Ask(question), Context(runId, teamId), CancellationToken.None);

        execution.HumanWaitToken.ShouldNotBeNull("the obligation holds — the run parks even with no card surface");

        var wait = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
            .SingleAsync(w => w.RunId == runId && w.IterationKey.EndsWith("#ask"));
        wait.Status.ShouldBe(WorkflowWaitStatuses.Pending, "the durable wait is what the ask API / Room resolves by token — no conversation was ever required to answer");
        wait.Token.ShouldBe(execution.HumanWaitToken);
    }

    [Fact]
    public async Task A_content_ask_with_no_surface_still_degrades()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        using var scope = _fixture.BeginScope();

        var execution = await scope.Resolve<ISupervisorActionExecutor>()
            .ExecuteAsync(Ask("which database should I target?"), Context(runId, teamId), CancellationToken.None);

        execution.HumanWaitToken.ShouldBeNull("a clarifying question must not hang an unattended run — the degrade stands");
        JsonDocument.Parse(execution.OutcomeJson!).RootElement.GetProperty("askHuman").GetString().ShouldBe("no-conversation");

        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId))
            .ShouldBe(0, "no wait is staged for a degraded content ask");
    }

    private static SupervisorDecision Ask(string question) => new()
    {
        Kind = SupervisorDecisionKinds.AskHuman,
        ServerAuthored = true,
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = question }, AgentJson.Options),
    };

    private static SupervisorTurnContext Context(Guid runId, Guid teamId) =>
        new() { Goal = "g", NodeId = "sup", TurnNumber = 3, SupervisorRunId = runId, TeamId = teamId, ConversationId = null, PriorDecisions = Array.Empty<SupervisorPriorDecision>() };

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "gate-park-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"g"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition>
                {
                    new() { From = "start", To = "sup" },
                    new() { From = "sup", To = "end" },
                },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }
}
