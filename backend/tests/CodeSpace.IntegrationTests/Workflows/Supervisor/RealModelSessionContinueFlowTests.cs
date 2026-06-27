using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// THE real-model SESSION CONTEXT-HANDOFF gate — does the live brain actually USE the prior-turn context handed to it
/// across a multi-run thread? It drives the REAL <see cref="LlmSupervisorDecider"/> over each
/// <see cref="SessionContinueGoldenScenarios"/> continuing-turn context (a follow-up composed over a mixed-mode
/// prior-turn digest, EXACTLY as the projection injects it) and scores the decision: the brain must NOT re-do
/// already-shipped work, and must address the NEW ask building on prior turns. A <see cref="Theory"/> over BOTH wires
/// (Anthropic gating + OpenAI informational); HONESTLY GATED on the <c>CODESPACE_LLM_*</c> secrets (absent → skip, so
/// CI/forks stay green). The rubric + that the digest reaches the prompt are pinned always-on by
/// <c>SessionContinueEvalTests</c>, so a failure here is the MODEL's context-handoff judgment, not a broken harness.
/// </summary>
[Trait("Category", "RealModel")]
public sealed class RealModelSessionContinueFlowTests
{
    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task The_real_model_uses_the_session_handoff_at_every_continue_point(string provider)
    {
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)

        // Non-gating on gateway latency (timeout/transport drop → informational), gating on a genuine wrong decision —
        // the blessed wire fails only when the brain ignores the handed-off context (redoes shipped work / misses the ask).
        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var credential = new ResolvedModelCredential { Provider = provider, BaseUrl = BaseUrlFor(provider, baseUrl), ApiKey = apiKey };
            var registry = new LLMClientRegistry(new ILLMClient[] { new AnthropicClient(SharedHttp), new OpenAiClient(SharedHttp) });
            var decider = new LlmSupervisorDecider(registry, new FixedCredentialSelector(model, credential), new CodeSpace.Core.Services.Agents.AgentHarnessRegistry(System.Array.Empty<CodeSpace.Core.Services.Agents.IAgentHarness>()), RealModelLiveWire.Personas());

            var scores = new List<SupervisorDecisionScore>();
            foreach (var scenario in SessionContinueGoldenScenarios.All)
            {
                var decision = await decider.DecideAsync(scenario.Context, CancellationToken.None);
                scores.Add(SupervisorDecisionEval.Score(scenario, decision));
            }

            var (passed, total, allPassed) = SupervisorDecisionEval.Aggregate(scores);
            var failures = string.Join(" | ", scores.Where(s => !s.Pass).Select(s => $"{s.Scenario}: got '{s.ActualKind}' — {s.Note}"));

            return (allPassed, $"{provider} model '{model}' scored {passed}/{total} session-continue handoff decisions. Failures: {failures}");
        });
    }

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
        // Matches the sibling decision-eval timeout: this gateway's progressive double-attempt runs ~50-90s/call, so
        // 150s fits a normal slow turn with headroom; a call that STILL exceeds it is non-gating gateway infra via
        // AssessLiveAsync (not a flaky RED).
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(150) };
    }

    private sealed class FixedCredentialSelector : IModelPoolSelector
    {
        private readonly string _model;
        private readonly ResolvedModelCredential _credential;
        public FixedCredentialSelector(string model, ResolvedModelCredential credential) { _model = model; _credential = credential; }

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(new ModelPoolPick { ModelId = _model, Credential = _credential });

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>>(System.Array.Empty<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
