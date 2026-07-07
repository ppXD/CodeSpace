using CodeSpace.Core.Services.Supervisor.Deciders;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// THE real-model kill-gate (measure-the-intelligence): drives the REAL <see cref="LlmSupervisorDecider"/> against
/// a REAL endpoint for every golden scenario and scores its decisions. A <see cref="Theory"/> over BOTH wires
/// (Anthropic + OpenAI) exercises both <see cref="IStructuredLLMClient"/>s from ONE set of secrets — the gateway is
/// assumed to serve both wires (e.g. LiteLLM); the per-provider base URL is derived from the single
/// <c>CODESPACE_LLM_BASE_URL</c>. HONESTLY GATED: early-returns when the <c>CODESPACE_LLM_*</c> secrets are absent
/// (CI/forks without them stay green), so it ACTIVATES only in a deployment that bound them. No cassette, no
/// Postgres — it constructs the real clients directly and calls the live API, so each run is a fresh real
/// measurement of decision quality. The scenarios are deliberately OBVIOUS, so a competent model gets them all;
/// a failure names the scenario + the wrong decision so it is diagnosable.
/// </summary>
[Trait("Category", "RealModel")]
public sealed class RealModelSupervisorDecisionFlowTests
{
    public const string BaseUrlEnvVar = "CODESPACE_LLM_BASE_URL";
    public const string ApiKeyEnvVar = "CODESPACE_LLM_API_KEY";
    public const string ModelIdEnvVar = "CODESPACE_LLM_MODEL_ID";

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task The_real_model_makes_the_right_decision_at_every_golden_point(string provider)
    {
        var baseUrl = RealModelLiveWire.Env(BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)

        // Non-gating on gateway latency: a per-call HTTP timeout / unreachable gateway is surfaced as informational, not
        // a RED — the blessed wire fails only on a genuine WRONG DECISION. (The gateway is sometimes slow enough to blow
        // even the generous per-call timeout below; that must not flake the kill-gate.)
        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var credential = RealModelLiveWire.Credential(provider, baseUrl, apiKey);
            var decider = new LlmSupervisorDecider(RealModelLiveWire.Registry(), RealModelLiveWire.Selector(model, credential), new CodeSpace.Core.Services.Agents.AgentHarnessRegistry(System.Array.Empty<CodeSpace.Core.Services.Agents.IAgentHarness>()), RealModelLiveWire.Personas(), new InMemoryTapeSummaryStore());

            var scores = new List<SupervisorDecisionScore>();
            foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
            {
                var decision = await decider.DecideAsync(scenario.Context, CancellationToken.None);
                scores.Add(SupervisorDecisionEval.Score(scenario, decision));
            }

            var (passed, total, allPassed) = SupervisorDecisionEval.Aggregate(scores);
            var failures = string.Join(" | ", scores.Where(s => !s.Pass).Select(s => $"{s.Scenario}: got '{s.ActualKind}' — {s.Note}"));

            return (allPassed, $"{provider} model '{model}' scored {passed}/{total} golden supervisor decisions. Failures: {failures}");
        });
    }
}
