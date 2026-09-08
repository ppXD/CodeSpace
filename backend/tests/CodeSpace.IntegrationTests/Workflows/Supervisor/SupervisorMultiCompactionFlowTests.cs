using System.Text.Json;
using Autofac;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>Production decider plus real PostgreSQL proof that rolling compaction survives fresh service scopes.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorMultiCompactionFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorMultiCompactionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Two_proactive_compactions_roll_forward_across_fresh_scopes_without_reintroducing_folded_rows()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();
        var modelRowId = Guid.NewGuid();
        var client = new RollingSummaryClient();
        var tape = Tape(16);

        using (var firstScope = _fixture.BeginScope())
        {
            var first = Decider(firstScope.Resolve<ISupervisorTapeSummaryStore>(), client, modelRowId);
            await first.DecideAsync(Context(runId, teamId, modelRowId, tape[..12]), CancellationToken.None);
        }

        using (var secondScope = _fixture.BeginScope())
        {
            var second = Decider(secondScope.Resolve<ISupervisorTapeSummaryStore>(), client, modelRowId);
            await second.DecideAsync(Context(runId, teamId, modelRowId, tape), CancellationToken.None);
        }

        using var verifyScope = _fixture.BeginScope();
        var persisted = await verifyScope.Resolve<ISupervisorTapeSummaryStore>().GetAsync(runId, teamId, CancellationToken.None);
        persisted.ShouldNotBeNull();
        persisted!.UpToSequence.ShouldBe(8, "the first scope folds 1..4 and the fresh scope advances the same durable row through 5..8");
        persisted.Text.ShouldBe("ROLLING-DIGEST-2");

        client.SummaryRequests.Count.ShouldBe(2);
        client.SummaryRequests[1].UserPrompt.ShouldContain("ROLLING-DIGEST-1", customMessage: "the fresh decider loaded and rolled forward the durable prior digest");
        client.SummaryRequests[1].UserPrompt.ShouldContain("marker-5-seq");
        client.SummaryRequests[1].UserPrompt.ShouldContain("marker-8-seq");
        client.SummaryRequests[1].UserPrompt.ShouldNotContain("marker-9-seq", customMessage: "the raw eight-decision tail is not baked into the digest");

        var finalDecisionPrompt = client.DecisionRequests[^1].UserPrompt;
        finalDecisionPrompt.ShouldContain("ROLLING-DIGEST-2");
        finalDecisionPrompt.ShouldNotContain("marker-8-seq", customMessage: "the twice-folded head is represented only by the new digest");
        finalDecisionPrompt.ShouldContain("marker-9-seq");
        finalDecisionPrompt.ShouldContain("marker-16-seq");
    }

    private static LlmSupervisorDecider Decider(ISupervisorTapeSummaryStore store, RollingSummaryClient client, Guid modelRowId)
    {
        var credential = new ResolvedModelCredential { Provider = client.Provider, ApiKey = "test" };
        return new LlmSupervisorDecider(new LLMClientRegistry([client]), RealModelLiveWire.Selector("opaque-model", credential, contextWindowTokens: 1), new AgentHarnessRegistry(Array.Empty<IAgentHarness>()), RealModelLiveWire.Personas(), store, new NullRepoGrounding(), NullLogger<LlmSupervisorDecider>.Instance);
    }

    private static SupervisorTurnContext Context(Guid runId, Guid teamId, Guid modelRowId, IReadOnlyList<SupervisorPriorDecision> tape) => new()
    {
        Goal = "Continue the durable run.",
        SupervisorRunId = runId,
        TeamId = teamId,
        SupervisorModelId = modelRowId,
        SupervisorModelPinned = true,
        TurnNumber = tape.Count,
        PriorDecisions = tape,
    };

    private static SupervisorPriorDecision[] Tape(int count) => Enumerable.Range(1, count).Select(i => new SupervisorPriorDecision
    {
        Id = Guid.NewGuid(),
        Sequence = i,
        DecisionKind = SupervisorDecisionKinds.AskHuman,
        Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = JsonSerializer.Serialize(new { note = $"marker-{i}-seq" }),
        OutcomeJson = "{}",
    }).ToArray();

    private sealed class RollingSummaryClient : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "TestRollingSummary";
        public List<StructuredLLMCompletionRequest> SummaryRequests { get; } = [];
        public List<StructuredLLMCompletionRequest> DecisionRequests { get; } = [];

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            if (request.SystemPrompt.StartsWith("You compact", StringComparison.Ordinal))
            {
                SummaryRequests.Add(request);
                return Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(new { summary = $"ROLLING-DIGEST-{SummaryRequests.Count}" }), Model = request.Model });
            }

            DecisionRequests.Add(request);
            var modelDecision = new SupervisorModelDecision
            {
                Kind = SupervisorDecisionKinds.Plan,
                Plan = new SupervisorPlanPayload { Subtasks = [new SupervisorPlannedSubtask { Id = "continue", Title = "Continue", Instruction = "Continue the run." }] },
            };
            return Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(modelDecision, SupervisorDecisionSchema.Options), Model = request.Model });
        }
    }
}
