using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the shared in-process-plane resolution (the planner + the effort classifier both use it): it iterates the
/// registered structured clients and returns the FIRST whose provider the team has a credentialed model for — so a team
/// whose pool is ALL one provider (e.g. all Custom-gateway models) resolves THAT provider's client + model, not a
/// provider-blind first pick that would find no model and fail. Null when no registered structured provider has a team
/// model.
/// </summary>
[Trait("Category", "Unit")]
public class InProcessStructuredModelTests
{
    [Fact]
    public async Task It_skips_a_provider_the_team_has_no_model_for_and_resolves_one_it_does()
    {
        // Registry order is OpenAI then Custom; the team has a model ONLY under Custom → resolve the Custom client + model,
        // never the (provider-blind) first OpenAI client that would find no model.
        var clients = new FakeRegistry(new FakeStructured("OpenAI"), new FakeStructured("Custom"));
        var models = new ProviderAwareSelector(hasModelFor: "Custom");

        var resolved = await InProcessStructuredModel.ResolveAsync(clients, models, Guid.NewGuid(), CancellationToken.None);

        resolved.ShouldNotBeNull();
        resolved!.Value.Client.Provider.ShouldBe("Custom", "an all-Custom pool resolves the Custom client, not the first-registered OpenAI one");
        resolved.Value.Pick.ModelId.ShouldBe("Custom-model");
    }

    [Fact]
    public async Task It_returns_null_when_no_registered_provider_has_a_team_model()
    {
        var clients = new FakeRegistry(new FakeStructured("OpenAI"), new FakeStructured("Anthropic"));
        var models = new ProviderAwareSelector(hasModelFor: "Custom");   // team only has Custom, but no Custom client registered

        (await InProcessStructuredModel.ResolveAsync(clients, models, Guid.NewGuid(), CancellationToken.None))
            .ShouldBeNull("no registered structured provider has a team model → the caller degrades / fails cleanly");
    }

    // ── Fakes ──

    private sealed class FakeRegistry : ILLMClientRegistry
    {
        public FakeRegistry(params IStructuredLLMClient[] structured) => All = structured.Cast<ILLMClient>().ToList();
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All.First();
    }

    private sealed class FakeStructured : ILLMClient, IStructuredLLMClient
    {
        public FakeStructured(string provider) => Provider = provider;
        public string Provider { get; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Returns a pick ONLY when asked for the one provider the team has a model under — the provider-match the resolver iterates to find.</summary>
    private sealed class ProviderAwareSelector : IModelPoolSelector
    {
        private readonly string _hasModelFor;
        public ProviderAwareSelector(string hasModelFor) => _hasModelFor = hasModelFor;

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken ct) =>
            Task.FromResult(string.Equals(provider, _hasModelFor, StringComparison.OrdinalIgnoreCase)
                ? new ModelPoolPick { ModelId = $"{provider}-model", Credential = new ResolvedModelCredential { Provider = provider, ApiKey = "k" } }
                : null);

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken ct) => Task.FromResult<ModelPoolPick?>(null);
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken ct) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<PoolModelInfo>>(Array.Empty<PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken ct) => Task.FromResult<Guid?>(null);
    }
}
