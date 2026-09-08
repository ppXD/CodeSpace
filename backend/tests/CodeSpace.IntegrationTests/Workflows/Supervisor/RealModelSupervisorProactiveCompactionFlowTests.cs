using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor.Deciders;
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

    private static SupervisorPriorDecision PriorDecision(int sequence) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = sequence,
        DecisionKind = SupervisorDecisionKinds.AskHuman,
        Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = JsonSerializer.Serialize(new { question = $"checkpoint-{sequence}" }),
        OutcomeJson = JsonSerializer.Serialize(new { answer = $"continue after checkpoint {sequence}" }),
    };
}
