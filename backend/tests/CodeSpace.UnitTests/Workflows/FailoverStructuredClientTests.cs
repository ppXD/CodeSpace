using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Agents;
using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: L4 pool failover. Pins: a team with models under several structured providers resolves a failover client
/// over ALL of them (registry order, first pick returned) while a single-provider team resolves the raw client
/// byte-identically; a transient / rate-limit fault hops to the next provider WITH ITS OWN credential and records the
/// skip on the trail (the answering model rides Model — never the resolved pick); a non-transient fault (auth, bad
/// request) propagates immediately without touching the alternate; when every candidate is down the LAST typed fault
/// propagates verbatim (no silent loop); the failover-worthy category set is closed.
/// </summary>
[Trait("Category", "Unit")]
public class FailoverStructuredClientTests
{
    [Fact]
    public async Task Several_providers_resolve_a_failover_client_over_all_of_them_first_pick_returned()
    {
        var anthropic = new ScriptedStructured("Anthropic", Answer);
        var openai = new ScriptedStructured("OpenAI", Answer);

        var resolved = await InProcessStructuredModel.ResolveAsync(new FakeRegistry(anthropic, openai), new MultiProviderSelector("Anthropic", "OpenAI"), Guid.NewGuid(), CancellationToken.None);

        var failover = resolved!.Value.Client.ShouldBeOfType<FailoverStructuredClient>();
        failover.Candidates.Count.ShouldBe(2);
        failover.Provider.ShouldBe("Anthropic", "the first candidate is what a request is built for");
        resolved.Value.Pick.ModelId.ShouldBe("Anthropic-model");
    }

    [Fact]
    public async Task A_single_provider_resolves_the_raw_client_byte_identically()
    {
        var only = new ScriptedStructured("Anthropic", Answer);

        var resolved = await InProcessStructuredModel.ResolveAsync(new FakeRegistry(only, new ScriptedStructured("OpenAI", Answer)), new MultiProviderSelector("Anthropic"), Guid.NewGuid(), CancellationToken.None);

        resolved!.Value.Client.ShouldBeSameAs(only, "no alternate exists — no wrapper, no behavior change");
    }

    [Fact]
    public async Task A_rate_limited_first_provider_hops_to_the_next_with_its_own_credential_and_records_the_skip()
    {
        var anthropic = new ScriptedStructured("Anthropic", _ => throw new LlmApiException("Anthropic", 429, LlmErrorCategory.RateLimited, "No deployments available"));
        var openai = new ScriptedStructured("OpenAI", Answer);
        var client = new FailoverStructuredClient(new[] { (anthropic as IStructuredLLMClient, Pick("Anthropic")), (openai, Pick("OpenAI")) });

        var completion = await client.CompleteStructuredAsync(Request("Anthropic-model"), CancellationToken.None);

        completion.Model.ShouldBe("OpenAI-model", "the ANSWERING model — a provenance stamp must read this, never the resolved pick");
        completion.FailedOver.ShouldHaveSingleItem().ShouldContain("Anthropic:Anthropic-model — RateLimited 429", customMessage: "the hop is recorded, never silent");
        openai.Requests.ShouldHaveSingleItem().Credential!.Provider.ShouldBe("OpenAI", "the alternate runs on ITS credential, not the failed provider's");
        openai.Requests[0].Model.ShouldBe("OpenAI-model");
    }

    [Fact]
    public async Task A_non_transient_fault_propagates_without_touching_the_alternate()
    {
        var anthropic = new ScriptedStructured("Anthropic", _ => throw new LlmApiException("Anthropic", 401, LlmErrorCategory.AuthFailed, "bad key"));
        var openai = new ScriptedStructured("OpenAI", Answer);
        var client = new FailoverStructuredClient(new[] { (anthropic as IStructuredLLMClient, Pick("Anthropic")), (openai, Pick("OpenAI")) });

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(Request("Anthropic-model"), CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.AuthFailed, "a dead credential is an operator fault — hopping providers would hide it behind a lucky alternate");
        openai.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task When_every_candidate_is_down_the_last_typed_fault_propagates_verbatim()
    {
        var anthropic = new ScriptedStructured("Anthropic", _ => throw new LlmApiException("Anthropic", 503, LlmErrorCategory.Transient, "upstream down"));
        var openai = new ScriptedStructured("OpenAI", _ => throw new LlmApiException("OpenAI", 429, LlmErrorCategory.RateLimited, "throttled"));
        var client = new FailoverStructuredClient(new[] { (anthropic as IStructuredLLMClient, Pick("Anthropic")), (openai, Pick("OpenAI")) });

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(Request("Anthropic-model"), CancellationToken.None));

        ex.Provider.ShouldBe("OpenAI", "the LAST candidate's fault is the one the caller parks on — the whole pool was tried, once each, no loop");
        anthropic.Requests.Count.ShouldBe(1);
        openai.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(LlmErrorCategory.Transient, true)]
    [InlineData(LlmErrorCategory.RateLimited, true)]
    [InlineData(LlmErrorCategory.AuthFailed, false)]
    [InlineData(LlmErrorCategory.BadRequest, false)]
    [InlineData(LlmErrorCategory.ContextLengthExceeded, false)]
    [InlineData(LlmErrorCategory.ContentFiltered, false)]
    [InlineData(LlmErrorCategory.Malformed, false)]
    public void Only_wire_health_faults_are_failover_worthy(LlmErrorCategory category, bool expected)
    {
        FailoverStructuredClient.IsFailoverWorthy(category).ShouldBe(expected);
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private static Task<StructuredLLMCompletion> Answer(StructuredLLMCompletionRequest request) =>
        Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(new { ok = true }), Model = request.Model });

    private static ModelPoolPick Pick(string provider) => new() { ModelId = $"{provider}-model", Credential = new ResolvedModelCredential { Provider = provider, ApiKey = $"{provider}-key" } };

    private static StructuredLLMCompletionRequest Request(string model) => new() { Model = model, SystemPrompt = "s", UserPrompt = "u", JsonSchema = JsonSerializer.SerializeToElement(new { type = "object" }) };

    private sealed class ScriptedStructured : ILLMClient, IStructuredLLMClient
    {
        private readonly Func<StructuredLLMCompletionRequest, Task<StructuredLLMCompletion>> _behavior;
        public ScriptedStructured(string provider, Func<StructuredLLMCompletionRequest, Task<StructuredLLMCompletion>> behavior) { Provider = provider; _behavior = behavior; }
        public string Provider { get; }
        public List<StructuredLLMCompletionRequest> Requests { get; } = new();
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return _behavior(request);
        }
    }

    private sealed class FakeRegistry : ILLMClientRegistry
    {
        public FakeRegistry(params IStructuredLLMClient[] structured) => All = structured.Cast<ILLMClient>().ToList();
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All.First();
    }

    /// <summary>Returns a pick for every provider in the given set — the multi-provider pool the failover exists for.</summary>
    private sealed class MultiProviderSelector : IModelPoolSelector
    {
        private readonly HashSet<string> _providers;
        public MultiProviderSelector(params string[] providers) => _providers = new HashSet<string>(providers, StringComparer.OrdinalIgnoreCase);

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken ct) =>
            Task.FromResult(_providers.Contains(provider) ? Pick(provider) : null);

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken ct) => Task.FromResult<ModelPoolPick?>(null);
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken ct) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<PoolModelInfo>>(Array.Empty<PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken ct) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
