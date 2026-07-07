using CodeSpace.Core.Services.Supervisor.Deciders;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The real-model proof that a Custom-tagged credential runs all the way to the SUPERVISOR brain: the SAME live gateway
/// tagged <c>Provider="Custom"</c> routes to the Custom OpenAI-compatible client, and the live model scores the golden
/// supervisor decisions correctly — so "Custom endpoints run to the supervisor" end to end on a real endpoint, not just
/// the agent harness. HONESTLY GATED on <c>CODESPACE_LLM_*</c> (skips green without). Constructs the real client directly
/// (no Postgres) so each run is a fresh live measurement.
/// </summary>
[Trait("Category", "RealModel")]
public sealed class RealModelCustomProviderFlowTests
{
    [Fact]
    public async Task A_Custom_tagged_credential_drives_the_live_supervisor_brain_correctly()
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)

        await RealModelGate.AssessLiveAsync("Custom", async () =>
        {
            // Tag the SAME gateway as Provider="Custom" → the decider's provider-match resolves the Custom client.
            var credential = RealModelLiveWire.Credential("Custom", baseUrl, apiKey);
            var decider = new LlmSupervisorDecider(RealModelLiveWire.Registry(), RealModelLiveWire.Selector(model, credential), new CodeSpace.Core.Services.Agents.AgentHarnessRegistry(System.Array.Empty<CodeSpace.Core.Services.Agents.IAgentHarness>()), RealModelLiveWire.Personas(), new InMemoryTapeSummaryStore());

            var scores = new List<SupervisorDecisionScore>();
            foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
            {
                var decision = await decider.DecideAsync(scenario.Context, CancellationToken.None);
                scores.Add(SupervisorDecisionEval.Score(scenario, decision));
            }

            var (passed, total, allPassed) = SupervisorDecisionEval.Aggregate(scores);
            var failures = string.Join(" | ", scores.Where(s => !s.Pass).Select(s => $"{s.Scenario}: got '{s.ActualKind}' — {s.Note}"));

            return (allPassed, $"Custom-tagged gateway model '{model}' scored {passed}/{total} golden supervisor decisions through the Custom in-process client. Failures: {failures}");
        });
    }
}
