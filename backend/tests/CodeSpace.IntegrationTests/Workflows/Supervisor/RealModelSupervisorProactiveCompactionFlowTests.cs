using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Live proof that a credentialed model row's declared context capacity drives production preflight compaction:
/// the same real model first writes the durable rolling digest, then authors a decision from the compacted tape.
/// The deliberately tiny capacity is test data for the generic row-level contract; no model id or task phrase is
/// recognized by production code. Gateway faults remain honest non-measurements under <see cref="RealModelGate"/>.
/// </summary>
[Trait("Category", "RealModel")]
public sealed class RealModelSupervisorProactiveCompactionFlowTests
{
    [SkippableFact]
    public async Task The_real_model_compacts_before_the_decision_call_when_its_selected_row_declares_a_tight_window()
    {
        const string provider = "OpenAI";
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);
        if (baseUrl is null || apiKey is null || model is null) throw RealModelGate.ReportSkipped(provider, "CODESPACE_LLM_* absent; proactive compaction was not measured");

        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var modelRowId = Guid.NewGuid();
            var store = new InMemoryTapeSummaryStore();
            var credential = RealModelLiveWire.Credential(provider, baseUrl, apiKey);
            var decider = new LlmSupervisorDecider(RealModelLiveWire.Registry(), RealModelLiveWire.Selector(model, credential, contextWindowTokens: 1), new AgentHarnessRegistry(Array.Empty<IAgentHarness>()), RealModelLiveWire.Personas(), store, new NullRepoGrounding(), new ConsoleTestLogger<LlmSupervisorDecider>());
            var context = new SupervisorTurnContext
            {
                Goal = "Review the accumulated run history and choose the best next action.",
                SupervisorRunId = Guid.NewGuid(),
                TeamId = Guid.NewGuid(),
                SupervisorModelId = modelRowId,
                SupervisorModelPinned = true,
                TurnNumber = 12,
                PriorDecisions = Enumerable.Range(1, 12).Select(PriorDecision).ToArray(),
            };

            var decision = await decider.DecideAsync(context, CancellationToken.None);
            var summary = store.Current;
            var compacted = summary is { UpToSequence: 4 } && !string.IsNullOrWhiteSpace(summary.Text);
            var modelAuthoredDecision = decision.Usage is not null;

            return (compacted && modelAuthoredDecision, $"proactive digest persisted={compacted}; compacted through sequence={summary?.UpToSequence}; post-compaction model decision authored={modelAuthoredDecision}; decision kind={decision.Kind}");
        });
    }

    [SkippableFact]
    public async Task The_real_model_preserves_opaque_obligations_through_two_equal_budget_compactions_and_a_fresh_decider()
    {
        const string provider = "OpenAI";
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);
        if (baseUrl is null || apiKey is null || model is null) throw RealModelGate.ReportSkipped(provider, "CODESPACE_LLM_* absent; multi-compaction semantic retention was not measured");

        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var modelRowId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var teamId = Guid.NewGuid();
            var store = new InMemoryTapeSummaryStore();
            var credential = RealModelLiveWire.Credential(provider, baseUrl, apiKey);
            var live = (IStructuredLLMClient)RealModelLiveWire.Registry().Resolve(provider);
            var hybrid = new LiveSummaryDeterministicDecisionClient(live);
            var registry = new LLMClientRegistry([hybrid]);
            var selector = RealModelLiveWire.Selector(model, credential, contextWindowTokens: 1);
            var tape = SemanticTape();

            var first = Decider(registry, selector, store);
            await first.DecideAsync(Context(runId, teamId, modelRowId, tape[..12]), CancellationToken.None);

            var restarted = Decider(registry, selector, store);
            await restarted.DecideAsync(Context(runId, teamId, modelRowId, tape), CancellationToken.None);

            var summary = store.Current;
            var equalBudgets = hybrid.SummaryRequests.Count == 2 && hybrid.SummaryRequests.All(r => r.MaxOutputTokens == 1024);
            var retained = summary is { UpToSequence: 8 }
                && summary.Text.Contains("OBLIGATION-ALPHA-7319", StringComparison.OrdinalIgnoreCase)
                && summary.Text.Contains("RECEIPT-OMEGA-4826", StringComparison.OrdinalIgnoreCase);

            return (equalBudgets && retained, $"two real summary calls={hybrid.SummaryRequests.Count}; equal 1024-token ceilings={equalBudgets}; durable boundary={summary?.UpToSequence}; original obligation and later receipt retained={retained}");
        });
    }

    private static LlmSupervisorDecider Decider(ILLMClientRegistry registry, CodeSpace.Core.Services.Agents.ModelCredentials.IModelPoolSelector selector, InMemoryTapeSummaryStore store) =>
        new(registry, selector, new AgentHarnessRegistry(Array.Empty<IAgentHarness>()), RealModelLiveWire.Personas(), store, new NullRepoGrounding(), new ConsoleTestLogger<LlmSupervisorDecider>());

    private static SupervisorTurnContext Context(Guid runId, Guid teamId, Guid modelRowId, IReadOnlyList<SupervisorPriorDecision> tape) => new()
    {
        Goal = "Honor every durable obligation and receipt while deciding the next step.",
        SupervisorRunId = runId,
        TeamId = teamId,
        SupervisorModelId = modelRowId,
        SupervisorModelPinned = true,
        TurnNumber = tape.Count,
        PriorDecisions = tape,
    };

    private static SupervisorPriorDecision[] SemanticTape() => Enumerable.Range(1, 16).Select(sequence => PriorDecision(sequence) with
    {
        PayloadJson = sequence switch
        {
            1 => JsonSerializer.Serialize(new { question = "Record OBLIGATION-ALPHA-7319: preserve the migration backup until verification passes." }),
            6 => JsonSerializer.Serialize(new { question = "Record external effect RECEIPT-OMEGA-4826 as already confirmed; never repeat that effect." }),
            _ => JsonSerializer.Serialize(new { question = $"ordinary checkpoint {sequence}" }),
        },
        OutcomeJson = JsonSerializer.Serialize(new { answer = $"acknowledged checkpoint {sequence}" }),
    }).ToArray();

    private static SupervisorPriorDecision PriorDecision(int sequence) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = sequence,
        DecisionKind = SupervisorDecisionKinds.AskHuman,
        Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = JsonSerializer.Serialize(new { question = $"checkpoint-{sequence}" }),
        OutcomeJson = JsonSerializer.Serialize(new { answer = $"continue after checkpoint {sequence}" }),
    };

    private sealed class LiveSummaryDeterministicDecisionClient : ILLMClient, IStructuredLLMClient
    {
        private readonly IStructuredLLMClient _live;

        public LiveSummaryDeterministicDecisionClient(IStructuredLLMClient live) { _live = live; }

        public string Provider => "OpenAI";
        public List<StructuredLLMCompletionRequest> SummaryRequests { get; } = [];

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            if (request.SystemPrompt.StartsWith("You compact", StringComparison.Ordinal))
            {
                SummaryRequests.Add(request);
                return await _live.CompleteStructuredAsync(request, cancellationToken);
            }

            var modelDecision = new SupervisorModelDecision
            {
                Kind = SupervisorDecisionKinds.Plan,
                Plan = new SupervisorPlanPayload { Subtasks = [new SupervisorPlannedSubtask { Id = "continue", Title = "Continue", Instruction = "Continue from the compacted state." }] },
            };
            return new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(modelDecision, SupervisorDecisionSchema.Options), Model = request.Model };
        }
    }
}
