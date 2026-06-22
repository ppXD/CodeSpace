using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Registers the named HttpClients the in-process LLM clients resolve by <c>CreateClient(nameof(Client))</c>. Extracted
/// from Startup so the SAME hardened registration is exercised by a resilience test (which overrides the primary
/// handler with a fake) — not re-derived. Each client gets: a GENEROUS, operator-tunable request budget
/// (<see cref="LlmHttpDefaults"/>) so a slow reasoning / long-generation model is not guillotined at the framework's
/// 100s default; a <see cref="SocketsHttpHandler"/> with <c>PooledConnectionLifetime</c> (picks up a gateway DNS
/// rotation) + redirects OFF (a downgrade redirect must not carry the key header); and a RETRY-ONLY resilience handler
/// (transient 5xx/408/429 + honor <c>Retry-After</c>, exponential backoff + jitter).
/// </summary>
public static class LlmHttpClientRegistration
{
    /// <summary>The names the LLM clients resolve — kept here so Startup + tests register/override the same set.</summary>
    public static readonly string[] ClientNames = { nameof(AnthropicClient), nameof(OpenAiClient) };

    /// <summary>The resilience handler name (one logical pipeline per client) — also the override key a test reconfigures.</summary>
    public const string ResilienceHandlerName = "llm-transport";

    public static IServiceCollection AddLlmHttpClients(this IServiceCollection services)
    {
        foreach (var name in ClientNames)
        {
            services.AddHttpClient(name, c => c.Timeout = LlmHttpDefaults.RequestTimeout)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    // A finite lifetime so a long-lived process picks up a gateway DNS/cert rotation; redirects off so a
                    // same-host https→http downgrade can't smuggle the api-key/Bearer header to a plaintext hop.
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    AllowAutoRedirect = false,
                })
                // RETRY ONLY — deliberately NO timeout strategy (the AddStandardResilienceHandler default 10s attempt /
                // 30s total would be WORSE than 100s for a long LLM generation; the real ceiling is HttpClient.Timeout +
                // the node's optional per-call budget) and NO circuit breaker (a shared breaker would bleed one team's
                // bad gateway onto every team — a per-endpoint-keyed breaker is a deferred refinement).
                .AddResilienceHandler(ResilienceHandlerName, b => b.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(1),
                    ShouldRetryAfterHeader = true,
                }));
        }

        return services;
    }
}
