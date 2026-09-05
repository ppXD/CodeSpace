using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Agents;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Workflows.Llm;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task A_hop_stamps_the_wire_health_cause_on_the_answer_and_announces_it()
    {
        // The trail alone cannot be ACTED on: it is prose. A caller that finds the substitute's answer unusable needs
        // the typed fault (category + status + Retry-After) to back off and park on, so the hop stamps it — and says so
        // out loud, because a silent hop is how a throttled pool reads as ordinary model behaviour in a log.
        var throttled = new LlmApiException("Anthropic", 429, LlmErrorCategory.RateLimited, "slow down", retryAfter: TimeSpan.FromSeconds(9));
        var anthropic = new ScriptedStructured("Anthropic", _ => throw throttled);
        var openai = new ScriptedStructured("OpenAI", Answer);
        var logger = new CapturingLogger();
        var client = new FailoverStructuredClient(new[] { (anthropic as IStructuredLLMClient, Pick("Anthropic")), (openai, Pick("OpenAI")) }, logger);

        var completion = await client.CompleteStructuredAsync(Request("Anthropic-model"), CancellationToken.None);

        completion.FailedOverCause.ShouldBeSameAs(throttled, "the answer must carry WHY the call left the caller's own model, typed — not just that it did");
        completion.FailedOverCause!.RetryAfter.ShouldBe(TimeSpan.FromSeconds(9), "the provider's own backoff hint rides along; a caller that re-throws this feeds the cause-aware retry");

        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("Anthropic:Anthropic-model");
        warning.Message.ShouldContain("RateLimited 429", customMessage: "the log names candidate → outcome, so an operator sees the hop without reading a completion record");
    }

    [Fact]
    public async Task A_first_candidate_that_answers_carries_no_cause()
    {
        var completion = await new FailoverStructuredClient(new[] { (new ScriptedStructured("Anthropic", Answer) as IStructuredLLMClient, Pick("Anthropic")), (new ScriptedStructured("OpenAI", Answer), Pick("OpenAI")) })
            .CompleteStructuredAsync(Request("Anthropic-model"), CancellationToken.None);

        completion.FailedOverCause.ShouldBeNull("nothing was skipped — a caller must not read a throttle into a clean first answer");
        completion.FailedOver.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_exhausted_pool_whose_last_fault_is_model_side_surfaces_the_throttle_that_started_the_hop()
    {
        // The shape that ended real-model run 33930904059's arm on a clean stop: the run's own brain was throttled, the
        // hop landed on a substitute, and the substitute's own MODEL-side fault is a category every caller fails CLOSED
        // on. Reporting that fault says "the model could not decide" about a model that was never asked — the honest
        // cause is the 429, and it is also the only one a retry/park can act on.
        var anthropic = new ScriptedStructured("Anthropic", _ => throw new LlmApiException("Anthropic", 429, LlmErrorCategory.RateLimited, "slow down", retryAfter: TimeSpan.FromSeconds(4)));
        var openai = new ScriptedStructured("OpenAI", _ => throw new LlmApiException("OpenAI", 400, LlmErrorCategory.BadRequest, "unsupported tool_choice"));
        var client = new FailoverStructuredClient(new[] { (anthropic as IStructuredLLMClient, Pick("Anthropic")), (openai, Pick("OpenAI")) });

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(Request("Anthropic-model"), CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.RateLimited, "a throttle that no alternate could cover is still a throttle — never a model-capability verdict");
        ex.Provider.ShouldBe("Anthropic", "the fault named is the one on the caller's OWN model");
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(4));
        ex.Message.ShouldContain("OpenAI:OpenAI-model", customMessage: "the substitute's own fault is not lost — it rides the message (and the inner exception) so the pool's real state is diagnosable");
        ex.InnerException.ShouldBeOfType<LlmApiException>().Category.ShouldBe(LlmErrorCategory.BadRequest);
    }

    [Fact]
    public async Task A_first_candidate_that_fails_model_side_still_propagates_verbatim_without_touching_the_alternate()
    {
        // The re-label above is scoped to a pool the call was FORCED off by wire health. With no hop behind it, a
        // model-side fault on the caller's own model is exactly what it looks like and must stay fail-closed.
        var anthropic = new ScriptedStructured("Anthropic", _ => throw new LlmApiException("Anthropic", 400, LlmErrorCategory.BadRequest, "bad schema"));
        var openai = new ScriptedStructured("OpenAI", Answer);
        var client = new FailoverStructuredClient(new[] { (anthropic as IStructuredLLMClient, Pick("Anthropic")), (openai, Pick("OpenAI")) });

        (await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(Request("Anthropic-model"), CancellationToken.None)))
            .Category.ShouldBe(LlmErrorCategory.BadRequest);

        openai.Requests.ShouldBeEmpty();
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

    // -- D1: an UNPRICED candidate under a cost cap is a skip, not a run-killer --

    [Fact]
    public async Task An_unpriced_first_candidate_is_SKIPPED_so_a_priced_alternate_still_answers()
    {
        // Drives the REAL guard inside the REAL failover loop - the exact composition production uses (the recording
        // decorator wraps each candidate's call in LlmBudgetGuard). Testing the guard alone with two sequential calls
        // cannot see this bug: the refusal has to ESCAPE the loop to kill the run, and only the loop can catch it.
        var anthropic = new ScriptedStructured("Anthropic", Answer);
        var openai = new ScriptedStructured("OpenAI", Answer);

        // Only the OpenAI candidate is priced, so the Anthropic one is refused before it is ever called.
        var prices = Priced("OpenAI-model");
        var client = new FailoverStructuredClient(new[] { (Guarded(anthropic, prices), Pick("Anthropic")), (Guarded(openai, prices), Pick("OpenAI")) });

        var completion = await client.CompleteStructuredAsync(Request("Anthropic-model"), CancellationToken.None);

        completion.Model.ShouldBe("OpenAI-model", "the priced alternate answered - an unpriced first pick must not kill the run (#1737/#1738)");
        anthropic.Requests.ShouldBeEmpty("the refusal precedes the call, so no money was spent on the unpriced candidate");
        completion.FailedOver.ShouldHaveSingleItem().ShouldContain("unpriced under the run's cost cap");
    }

    [Fact]
    public async Task A_pool_where_EVERY_candidate_is_unpriced_still_fails_closed()
    {
        // The skip is not a licence to spend blind: when nothing in the pool can be priced, the last candidate's
        // refusal propagates and the caller parks the run instead of running it past an unenforceable cap.
        var anthropic = new ScriptedStructured("Anthropic", Answer);
        var openai = new ScriptedStructured("OpenAI", Answer);
        var prices = Priced("something-else-entirely");
        var client = new FailoverStructuredClient(new[] { (Guarded(anthropic, prices), Pick("Anthropic")), (Guarded(openai, prices), Pick("OpenAI")) });

        var refusal = await Should.ThrowAsync<UnpricedModelUnderCapException>(() => client.CompleteStructuredAsync(Request("Anthropic-model"), CancellationToken.None));

        refusal.Model.ShouldBe("OpenAI-model", "the LAST candidate's refusal is the one that propagates");
        anthropic.Requests.ShouldBeEmpty();
        openai.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unpriced_candidate_under_NO_cap_is_never_skipped()
    {
        // The whole rule is scoped to a declared cap; without one the guard passes everything through, so the first
        // candidate answers exactly as it did before D1.
        var anthropic = new ScriptedStructured("Anthropic", Answer);
        var openai = new ScriptedStructured("OpenAI", Answer);
        var client = new FailoverStructuredClient(new[] { (Guarded(anthropic, ModelPriceResolver.Empty, cap: null), Pick("Anthropic")), (Guarded(openai, ModelPriceResolver.Empty, cap: null), Pick("OpenAI")) });

        (await client.CompleteStructuredAsync(Request("Anthropic-model"), CancellationToken.None)).Model.ShouldBe("Anthropic-model");

        anthropic.Requests.ShouldHaveSingleItem();
    }

    /// <summary>One candidate wrapped the way <c>RecordingStructuredLLMClientDecorator</c> wraps it in production: its call rides <see cref="LlmBudgetGuard"/> under a scope carrying the run's cap + the team's row prices.</summary>
    private static IStructuredLLMClient Guarded(IStructuredLLMClient inner, IReadOnlyDictionary<string, ModelPrice> prices, decimal? cap = 5m) =>
        new GuardedStructured(inner, prices, cap);

    private static IReadOnlyDictionary<string, ModelPrice> Priced(params string[] models) =>
        models.ToDictionary(m => m, _ => new ModelPrice { InputPerMillionUsd = 1m, OutputPerMillionUsd = 1m }, StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Mirrors the production decorator's one load-bearing line: every candidate call rides the budget guard, so an unpriced model is refused BEFORE the inner client is touched.</summary>
    private sealed class GuardedStructured : IStructuredLLMClient
    {
        private readonly IStructuredLLMClient _inner;
        private readonly LlmCallScope _scope;

        public GuardedStructured(IStructuredLLMClient inner, IReadOnlyDictionary<string, ModelPrice> prices, decimal? cap)
        {
            _inner = inner;
            _scope = new LlmCallScope(Guid.NewGuid(), Guid.NewGuid(), "sup", "k", "supervisor.decision", null!, null!, new CodeSpace.Tests.Fakes.AdmitAllBudgetLedger(), cap, prices);
        }

        public string Provider => _inner.Provider;

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) =>
            LlmBudgetGuard.GuardedAsync(_scope, request.Model, request.SystemPrompt, request.UserPrompt, request.MaxOutputTokens,
                inner => _inner.CompleteStructuredAsync(request, inner), _ => 0.01m, ct);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
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
