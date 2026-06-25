using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// THE real-model ROLLING-SUMMARY quality gate — does the live brain's distillation actually PRESERVE the older
/// turns it folds (so a long thread keeps its early memory)? It drives the REAL <see cref="SessionSummarizer"/>'s
/// distillation (DB-free, via the internal <c>TryDistillAsync</c>) over a handful of older turns each carrying a
/// distinctive entity, and scores the produced summary: every older turn's key entity must survive into the summary.
/// A <see cref="Theory"/> over BOTH wires (Anthropic gating + OpenAI informational); HONESTLY GATED on the
/// <c>CODESPACE_LLM_*</c> secrets (absent → skip, so CI/forks stay green). The fold/watermark/persist plumbing is pinned
/// always-on by <c>WorkSessionSummaryFlowTests</c> (faked LLM), so a failure HERE is the model's distillation quality —
/// dropping an older turn's work — not a broken harness.
/// </summary>
[Trait("Category", "RealModel")]
public sealed class RealModelSessionSummaryFlowTests
{
    // Distinctive entities, one per older turn — a faithful distillation keeps these proper nouns; dropping one means
    // that turn's work was lost from the thread's memory (the exact regression this gate guards on the blessed wire).
    private static readonly (string Entity, string Goal, string Result)[] OlderTurns =
    {
        ("Zephyr", "Add the Zephyr payment gateway integration", "Implemented ZephyrClient with idempotent retry + webhook verification"),
        ("Quokka", "Add the Quokka in-memory caching layer", "Wired QuokkaCache into the read API with a 60s TTL"),
        ("Nimbus", "Fix the Nimbus notification webhook", "Nimbus webhook now verifies HMAC signatures before dispatch"),
    };

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task The_real_model_distillation_preserves_every_older_turn(string provider)
    {
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)

        // Non-gating on gateway latency (timeout/transport drop → informational), gating on a genuine quality miss —
        // the blessed wire fails only when the distillation DROPS an older turn's work from the rolling summary.
        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var credential = new ResolvedModelCredential { Provider = provider, BaseUrl = BaseUrlFor(provider, baseUrl), ApiKey = apiKey };
            var registry = new LLMClientRegistry(new ILLMClient[] { new Core.Services.Workflows.Llm.Anthropic.AnthropicClient(SharedHttp), new Core.Services.Workflows.Llm.OpenAi.OpenAiClient(SharedHttp) });
            var summarizer = new SessionSummarizer(db: null!, registry, new FixedPoolSelector(model, credential), NullLogger<SessionSummarizer>.Instance);

            var turns = OlderTurns.Select((t, i) => new SessionSummarizer.TurnRow(
                i + 1, "Success",
                JsonSerializer.Serialize(new { summary = t.Result }),
                JsonSerializer.Serialize(new { goal = t.Goal }))).ToList();

            var summary = await summarizer.TryDistillAsync(Guid.NewGuid(), existingSummary: null, turns, CancellationToken.None);

            if (string.IsNullOrWhiteSpace(summary))
                return (false, $"{provider} model '{model}' produced an EMPTY summary");

            var missing = OlderTurns.Where(t => !summary.Contains(t.Entity, StringComparison.OrdinalIgnoreCase)).Select(t => t.Entity).ToList();

            return (missing.Count == 0, $"{provider} model '{model}' distilled {OlderTurns.Length - missing.Count}/{OlderTurns.Length} older turns' entities. Dropped: [{string.Join(", ", missing)}]");
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
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(150) };
    }

    /// <summary>Resolves the summarizer's plain-text model via <c>SelectAsync</c> (provider-routed) to the configured live model + credential — DB-free, for a distillation-only real call.</summary>
    private sealed class FixedPoolSelector : IModelPoolSelector
    {
        private readonly string _model;
        private readonly ResolvedModelCredential _credential;
        public FixedPoolSelector(string model, ResolvedModelCredential credential) { _model = model; _credential = credential; }

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(new ModelPoolPick { ModelId = _model, Credential = _credential });

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>>(System.Array.Empty<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
    }
}
