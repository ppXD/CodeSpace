using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Pins the split between <see cref="SessionSummarizer.TryDistillAsync"/> (best-effort — swallows ANY distillation
/// fault into null, byte-identical to production's pre-split behaviour) and <see cref="SessionSummarizer.DistillAsync"/>
/// (the inner call it now wraps — PROPAGATES the same fault instead). The split exists so a caller that must tell a
/// genuine gateway/transport fault apart from a genuinely empty completion can drive the throwing variant directly:
/// on real-model lane run 34112615353, a 429 mid-storm was swallowed into the same null an empty completion produces,
/// so the decision-eval job reported "produced an EMPTY summary" (a gating verdict) for what was actually a gateway
/// outage (which must only ever be a non-gating infra skip).
/// </summary>
[Trait("Category", "Unit")]
public sealed class SessionSummarizerDistillationTests
{
    private static readonly IReadOnlyList<SessionSummarizer.TurnRow> OneTurn = new[] { new SessionSummarizer.TurnRow(Guid.NewGuid(), 1, "Success", "build login", "added auth", LegacyBranch: null) };
    private static readonly Dictionary<Guid, IReadOnlyList<PublishManifest>> NoManifests = new();

    [Theory]
    [InlineData(LlmErrorCategory.Transient)]
    [InlineData(LlmErrorCategory.RateLimited)]
    [InlineData(LlmErrorCategory.AuthFailed)]
    [InlineData(LlmErrorCategory.Malformed)]
    [InlineData(LlmErrorCategory.BadRequest)]
    [InlineData(LlmErrorCategory.ContextLengthExceeded)]
    [InlineData(LlmErrorCategory.ContentFiltered)]
    public async Task TryDistillAsync_swallows_any_LlmApiException_category_into_null_with_a_warning_logged(LlmErrorCategory category)
    {
        var fault = new LlmApiException("Anthropic", 429, category, "gateway boom");
        var logger = new CapturingLogger();
        var summarizer = NewSummarizer(new FaultingLlmClient(fault), logger: logger);

        var result = await summarizer.TryDistillAsync(Guid.NewGuid(), existingSummary: null, OneTurn, NoManifests, CancellationToken.None);

        result.ShouldBeNull("best-effort: a summarization failure must never fail the launch — the digest falls back to the recent window");

        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Exception.ShouldBeSameAs(fault);
    }

    [Theory]
    [InlineData(LlmErrorCategory.Transient)]
    [InlineData(LlmErrorCategory.RateLimited)]
    [InlineData(LlmErrorCategory.AuthFailed)]
    [InlineData(LlmErrorCategory.Malformed)]
    [InlineData(LlmErrorCategory.BadRequest)]
    [InlineData(LlmErrorCategory.ContextLengthExceeded)]
    [InlineData(LlmErrorCategory.ContentFiltered)]
    public async Task DistillAsync_propagates_the_same_LlmApiException_TryDistillAsync_would_have_swallowed(LlmErrorCategory category)
    {
        var fault = new LlmApiException("Anthropic", 429, category, "gateway boom");
        var summarizer = NewSummarizer(new FaultingLlmClient(fault));

        var thrown = await Should.ThrowAsync<LlmApiException>(() =>
            summarizer.DistillAsync(Guid.NewGuid(), existingSummary: null, OneTurn, NoManifests, CancellationToken.None));

        thrown.ShouldBeSameAs(fault, "the real-model eval's RealModelGate.IsGatewayInfraFailure classifies the THROWN instance's Category — a re-wrap would still work, but there is no reason to");
    }

    [Fact]
    public async Task DistillAsync_returns_null_when_no_pool_model_resolves_without_throwing()
    {
        // The ONE non-exceptional null: no credentialed team model at all is a real fail-open case, not a fault —
        // it must stay a plain return, never get swept up into "propagate every fault" by an over-eager refactor.
        var summarizer = NewSummarizer(new FaultingLlmClient(new InvalidOperationException("must not be called — no model resolved")), selector: new NoModelSelector());

        var result = await summarizer.DistillAsync(Guid.NewGuid(), existingSummary: null, OneTurn, NoManifests, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("DISTILLED THREAD MEMORY")]
    [InlineData("")]
    public async Task DistillAsync_returns_the_models_text_verbatim_even_when_it_is_empty(string modelText)
    {
        // A genuinely empty completion is a MODEL quality miss, not a distillation fault — it must come back as a
        // plain return (never thrown) so a caller like the real-model eval can still score it as a failing VERDICT,
        // distinct from the infra skip a thrown LlmApiException produces.
        var summarizer = NewSummarizer(new CannedLlmClient(modelText));

        var result = await summarizer.DistillAsync(Guid.NewGuid(), existingSummary: null, OneTurn, NoManifests, CancellationToken.None);

        result.ShouldBe(modelText);
    }

    private static SessionSummarizer NewSummarizer(ILLMClient client, IModelPoolSelector? selector = null, ILogger<SessionSummarizer>? logger = null) =>
        new(db: null!, manifests: null!, new LLMClientRegistry(new[] { client }), selector ?? new FixedModelSelector(), logger ?? NullLogger<SessionSummarizer>.Instance);

    /// <summary>An LLM client whose call THROWS the given fault — models a gateway/transport failure surfacing from CompleteAsync.</summary>
    private sealed class FaultingLlmClient : ILLMClient
    {
        private readonly Exception _fault;
        public FaultingLlmClient(Exception fault) => _fault = fault;
        public string Provider => "Anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw _fault;
    }

    /// <summary>An LLM client that completes cleanly with a canned (possibly empty) text — models a genuine quality outcome, never a fault.</summary>
    private sealed class CannedLlmClient : ILLMClient
    {
        private readonly string _text;
        public CannedLlmClient(string text) => _text = text;
        public string Provider => "Anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = _text, Model = request.Model });
    }

    private sealed class FixedModelSelector : IModelPoolSelector
    {
        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(new ModelPoolPick { ModelId = "test-model", Credential = new ResolvedModelCredential { Provider = provider, ApiKey = "key" } });

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PoolModelInfo>>([]);
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class NoModelSelector : IModelPoolSelector
    {
        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(null);

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PoolModelInfo>>([]);
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class CapturingLogger : ILogger<SessionSummarizer>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
