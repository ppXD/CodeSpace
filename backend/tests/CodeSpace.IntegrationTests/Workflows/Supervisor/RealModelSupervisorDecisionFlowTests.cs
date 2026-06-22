using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Messages.Agents;

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
        var baseUrl = Env(BaseUrlEnvVar);
        var apiKey = Env(ApiKeyEnvVar);
        var model = Env(ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)

        // Non-gating on gateway latency: a per-call HTTP timeout / unreachable gateway is surfaced as informational, not
        // a RED — the blessed wire fails only on a genuine WRONG DECISION. (The gateway is sometimes slow enough to blow
        // even the generous per-call timeout below; that must not flake the kill-gate.)
        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var credential = new ResolvedModelCredential { Provider = provider, BaseUrl = BaseUrlFor(provider, baseUrl), ApiKey = apiKey };
            var registry = new LLMClientRegistry(new ILLMClient[] { new AnthropicClient(SharedHttp), new OpenAiClient(SharedHttp) });
            var decider = new LlmSupervisorDecider(registry, new FixedCredentialSelector(model, credential));

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

    /// <summary>Anthropic's client appends <c>/v1/messages</c> to the host base; OpenAI's appends <c>/chat/completions</c> to a <c>/v1</c> base — derive each from the single configured base so one gateway serves both wires.</summary>
    private static string BaseUrlFor(string provider, string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');

        if (!string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase)) return b;

        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b : b + "/v1";
    }

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);

    private static readonly IHttpClientFactory SharedHttp = new SimpleHttpClientFactory();

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        // A single golden decision is one structured call; on this gateway the progressive double-attempt (forced-tool
        // then a prompt-only floor) runs ~50-90s, so 150s fits a normal slow turn with headroom yet bounds this shared
        // job's runtime. A call that STILL exceeds it is non-gating gateway infra via AssessLiveAsync (not a flaky RED).
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(150) };
    }

    /// <summary>Resolves the brain model to the configured real model + credential (the in-process pool, stubbed for a decider-only real call).</summary>
    private sealed class FixedCredentialSelector : IModelPoolSelector
    {
        private readonly string _model;
        private readonly ResolvedModelCredential _credential;
        public FixedCredentialSelector(string model, ResolvedModelCredential credential) { _model = model; _credential = credential; }

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, bool requireStructured, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(new ModelPoolPick { ModelId = _model, Credential = _credential });

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, bool requireStructured, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
