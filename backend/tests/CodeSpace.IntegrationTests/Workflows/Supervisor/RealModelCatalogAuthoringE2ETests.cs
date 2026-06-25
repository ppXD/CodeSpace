using System.Linq;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Real-model E2E for the P1 capability catalog (intelligent Auto allocation). Drives the REAL supervisor decider on a
/// spawn-decision context WITH a rendered catalog whose default harness (codex-cli) CANNOT drive the only pooled model
/// (an Anthropic-provider model) — exactly the shape that produced the 12-agent "provider this harness cannot drive"
/// failure. Proves the catalog-informed LIVE brain still makes the right decision (spawn) given that catalog, i.e. the
/// catalog reaches the real model and does not degrade its decision. (The compatible (harness, model) PAIRING itself is
/// guaranteed deterministically by the authoring-time clamp + the run-time reconciler, both unit/integration-pinned;
/// this gate adds the live-brain signal the others can't.) HONESTLY GATED on the CODESPACE_LLM_* secrets — green on
/// CI/forks without them; activates only on the real-model lane.
/// </summary>
[Trait("Category", "RealModel")]
public sealed class RealModelCatalogAuthoringE2ETests
{
    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task The_real_model_decides_correctly_when_the_catalog_default_harness_mismatches_the_pool(string provider)
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)

        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var credential = RealModelLiveWire.Credential(provider, baseUrl, apiKey);

            // The catalog the brain will see: codex-cli drives OpenAI/Custom, claude-code drives Anthropic/Custom, and
            // the pool's only model is Anthropic-provider — so a naive codex default is the incompatible pairing.
            var harnesses = new AgentHarnessRegistry(new IAgentHarness[]
            {
                new CatalogHarness("codex-cli", "OpenAI", "Custom"),
                new CatalogHarness("claude-code", "Anthropic", "Custom"),
            });
            var selector = new PooledSelector(model, credential, new PoolModelInfo("metis-coder-max", "Anthropic"));
            var decider = new LlmSupervisorDecider(RealModelLiveWire.Registry(), selector, harnesses);

            // The 'planned, not spawned' golden context → the one reasonable next action is spawn.
            var scenario = SupervisorDecisionGoldenScenarios.All.First(s => s.AcceptedKinds.Contains(SupervisorDecisionKinds.Spawn));

            var decision = await decider.DecideAsync(scenario.Context, CancellationToken.None);

            var ok = scenario.AcceptedKinds.Contains(decision.Kind);
            return (ok, $"{provider} model '{model}' given a catalog whose default harness mismatches the only pooled model decided '{decision.Kind}' (expected one of {string.Join("/", scenario.AcceptedKinds)}).");
        });
    }

    private sealed class CatalogHarness : IAgentHarness, IModelCredentialProjector
    {
        public CatalogHarness(string kind, params string[] providers) { Kind = kind; SupportedProviders = providers; }

        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models => System.Array.Empty<string>();
        public IReadOnlyList<string> SupportedProviders { get; }

        public SandboxSpec BuildInvocation(AgentTask task) => throw new NotSupportedException();
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => throw new NotSupportedException();
        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) => throw new NotSupportedException();
    }

    private sealed class PooledSelector : IModelPoolSelector
    {
        private readonly ModelPoolPick _pick;
        private readonly IReadOnlyList<PoolModelInfo> _pool;

        public PooledSelector(string model, ResolvedModelCredential credential, params PoolModelInfo[] pool)
        {
            _pick = new ModelPoolPick { ModelId = model, Credential = credential };
            _pool = pool;
        }

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(_pick);
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(_pick);
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult(_pool);
    }
}
