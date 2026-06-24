using System.Net.Http;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The shared plumbing for the live real-model gates (the delivery-decision eval, the multi-turn trajectory, the
/// decision arbiter): read the <c>CODESPACE_LLM_*</c> secrets, derive the per-provider base URL from the single
/// configured host, build the real Anthropic + OpenAI structured-client registry over a generously-timed HttpClient,
/// and stub the model-pool selector to the configured live credential. ONE source of truth so the gates can never
/// drift on the wire setup (a fix here reaches every lane).
/// </summary>
internal static class RealModelLiveWire
{
    private static readonly IHttpClientFactory SharedHttp = new SimpleHttpClientFactory();

    /// <summary>The env var's value, or null when absent/blank — the honest self-skip signal (secrets unset ⇒ the gate skips green).</summary>
    public static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);

    /// <summary>The real Anthropic + OpenAI structured clients over the shared HttpClient — the registry the live decider/arbiter resolves its provider-routed client from.</summary>
    public static ILLMClientRegistry Registry() => new LLMClientRegistry(new ILLMClient[] { new AnthropicClient(SharedHttp), new OpenAiClient(SharedHttp) });

    /// <summary>The live credential for one wire: the provider + the per-provider base URL (derived from the single configured host) + the configured key.</summary>
    public static ResolvedModelCredential Credential(string provider, string baseUrl, string apiKey) =>
        new() { Provider = provider, BaseUrl = BaseUrlFor(provider, baseUrl), ApiKey = apiKey };

    /// <summary>A model-pool selector stubbed to the configured live model + credential (the in-process pool, for a decider/arbiter-only real call — no DB).</summary>
    public static IModelPoolSelector Selector(string model, ResolvedModelCredential credential) => new FixedCredentialSelector(model, credential);

    /// <summary>Anthropic's client appends <c>/v1/messages</c> to the host base; OpenAI's appends <c>/chat/completions</c> to a <c>/v1</c> base — derive each from the single configured base so one gateway serves both wires.</summary>
    private static string BaseUrlFor(string provider, string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');

        if (!string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase)) return b;

        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b : b + "/v1";
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        // A single live structured call (a decision or an arbiter verdict) runs the progressive double-attempt
        // (forced-tool → prompt-only floor) well under this generous per-call timeout; a call that STILL exceeds it is
        // non-gating gateway infra via RealModelGate.AssessLiveAsync, not a flaky RED.
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(150) };
    }

    /// <summary>Resolves the brain model to the configured real model + credential (the in-process pool, stubbed for a decider/arbiter-only real call).</summary>
    private sealed class FixedCredentialSelector : IModelPoolSelector
    {
        private readonly string _model;
        private readonly ResolvedModelCredential _credential;
        public FixedCredentialSelector(string model, ResolvedModelCredential credential) { _model = model; _credential = credential; }

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(new ModelPoolPick { ModelId = _model, Credential = _credential });

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
