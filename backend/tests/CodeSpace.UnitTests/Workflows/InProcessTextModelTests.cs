using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the provider/model pair used by plain-text in-process calls such as supervisor synthesis and rolling session
/// summaries. Registry order must not make a configured team model unreachable, and an authored model pin must be
/// presented unchanged to every provider candidate rather than silently discarded.
/// </summary>
[Trait("Category", "Unit")]
public sealed class InProcessTextModelTests
{
    [Fact]
    public async Task It_skips_a_provider_the_team_has_no_model_for_and_resolves_the_configured_provider()
    {
        var clients = new FakeRegistry(new FakeClient("OpenAI"), new FakeClient("Custom"));
        var models = new ProviderAwareSelector("Custom");

        var resolved = await InProcessTextModel.ResolveAsync(clients, models, Guid.NewGuid(), pinnedModel: null, CancellationToken.None);

        resolved.ShouldNotBeNull();
        resolved!.Value.Client.Provider.ShouldBe("Custom");
        resolved.Value.Pick.ModelId.ShouldBe("Custom-model");
        models.Providers.ShouldBe(new[] { "OpenAI", "Custom" });
    }

    [Fact]
    public async Task It_passes_the_exact_pin_to_each_candidate_and_fails_closed_when_none_match()
    {
        var clients = new FakeRegistry(new FakeClient("OpenAI"), new FakeClient("Anthropic"));
        var models = new ProviderAwareSelector(hasModelFor: "Custom");

        (await InProcessTextModel.ResolveAsync(clients, models, Guid.NewGuid(), "custom-model", CancellationToken.None)).ShouldBeNull();

        models.Pins.ShouldBe(new[] { "custom-model", "custom-model" });
    }

    [Fact]
    public async Task It_preserves_the_existing_text_only_preference_before_falling_back_to_structured_clients()
    {
        var clients = new FakeRegistry(new FakeStructuredClient("OpenAI"), new FakeClient("Custom"));
        var models = new MultiProviderSelector("OpenAI", "Custom");

        var resolved = await InProcessTextModel.ResolveAsync(clients, models, Guid.NewGuid(), pinnedModel: null, CancellationToken.None);

        resolved.ShouldNotBeNull();
        resolved!.Value.Client.Provider.ShouldBe("Custom", "plain-text synthesis preferred a text-only provider before this resolver became pool-aware");
        models.Providers.ShouldBe(new[] { "Custom" });
    }

    private sealed class FakeRegistry : ILLMClientRegistry
    {
        public FakeRegistry(params ILLMClient[] clients) => All = clients;
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All.Single(client => client.Provider == provider);
    }

    private sealed class FakeClient : ILLMClient
    {
        public FakeClient(string provider) => Provider = provider;
        public string Provider { get; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "ok", Model = request.Model });
    }

    private sealed class FakeStructuredClient : ILLMClient, IStructuredLLMClient
    {
        public FakeStructuredClient(string provider) => Provider = provider;
        public string Provider { get; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => Task.FromResult(new LLMCompletion { Text = "ok", Model = request.Model });
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ProviderAwareSelector : IModelPoolSelector
    {
        private readonly string _hasModelFor;
        public ProviderAwareSelector(string hasModelFor) => _hasModelFor = hasModelFor;
        public List<string> Providers { get; } = [];
        public List<string?> Pins { get; } = [];

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken)
        {
            Providers.Add(provider);
            Pins.Add(pinnedModel);
            return Task.FromResult(string.Equals(provider, _hasModelFor, StringComparison.OrdinalIgnoreCase)
                ? new ModelPoolPick { ModelId = $"{provider}-model", Credential = new ResolvedModelCredential { Provider = provider, ApiKey = "key" } }
                : null);
        }

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(null);
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PoolModelInfo>>([]);
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class MultiProviderSelector : IModelPoolSelector
    {
        private readonly HashSet<string> _providers;
        public MultiProviderSelector(params string[] providers) => _providers = new HashSet<string>(providers, StringComparer.OrdinalIgnoreCase);
        public List<string> Providers { get; } = [];

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken)
        {
            Providers.Add(provider);
            return Task.FromResult(_providers.Contains(provider)
                ? new ModelPoolPick { ModelId = $"{provider}-model", Credential = new ResolvedModelCredential { Provider = provider, ApiKey = "key" } }
                : null);
        }

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(null);
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PoolModelInfo>>([]);
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
