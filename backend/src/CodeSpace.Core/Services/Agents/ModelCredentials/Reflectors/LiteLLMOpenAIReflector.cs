using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.ModelCredentials.Reflectors;

/// <summary>
/// Reflects an OpenAI-compatible / LiteLLM gateway's <c>GET {baseUrl}/v1/models</c> into the credential's model list.
/// The ONLY case with a real HTTP plane the backend can hit: a gateway/proxy URL stored as <c>BaseUrl</c>. A direct
/// vendor key with no base URL (and every CLI-harness model) is NOT reflectable — those are manual only. Stateless
/// transport (mirrors <c>AnthropicClient</c>'s <c>IHttpClientFactory</c> use); it discovers the advertised model ids
/// (the pool is capability-generic — no flag to enrich). A curated per-vendor reflector would be a sibling here
/// (Rule 18.3) ONLY if a vendor ever needs metadata this generic path can't derive.
/// </summary>
public sealed class LiteLLMOpenAIReflector : IModelReflector, ISingletonDependency
{
    /// <summary>
    /// The named <c>HttpClient</c> this reflector uses — registered (Startup) with redirects DISABLED and a tight
    /// timeout. Redirects are off ON PURPOSE: the request carries the decrypted key as a Bearer header, and a
    /// same-host https→http downgrade redirect would NOT strip it (.NET only strips on cross-host), leaking the key in
    /// cleartext. A model-listing GET never legitimately redirects, so following one is all downside.
    /// </summary>
    public const string HttpClientName = "model-reflect";

    private readonly IHttpClientFactory _httpClientFactory;

    public LiteLLMOpenAIReflector(IHttpClientFactory httpClientFactory) { _httpClientFactory = httpClientFactory; }

    /// <summary>Reflectable exactly when a base URL is configured — that is the gateway endpoint we GET. A keyless gateway is fine (no auth header); a direct vendor key with no base URL is manual-only.</summary>
    public bool CanReflect(ResolvedModelCredential credential) => !string.IsNullOrWhiteSpace(credential.BaseUrl);

    public async Task<IReadOnlyList<ReflectedModel>> ListModelsAsync(ResolvedModelCredential credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUrl(credential.BaseUrl!));

        if (!string.IsNullOrEmpty(credential.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.ApiKey);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<OpenAIModelsResponse>(cancellationToken).ConfigureAwait(false);

        return (payload?.Data ?? Array.Empty<OpenAIModel>())
            .Select(d => d.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Select(id => new ReflectedModel { ModelId = id })
            .ToList();
    }

    /// <summary>
    /// Build the <c>/v1/models</c> endpoint from a base URL, robust to operator paste: strips any query / fragment,
    /// and tolerates a base that already ends in <c>/v1</c> (no <c>/v1/v1</c>) or in the full <c>/v1/models</c> (no
    /// <c>/models/v1/models</c>). A path prefix (a gateway mounted under <c>/llm</c>) is preserved.
    /// </summary>
    private static string BuildModelsUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed.TrimEnd('/') + "/v1/models";   // best-effort fallback; gateway URLs are absolute in practice

        var path = uri.AbsolutePath.TrimEnd('/');

        var modelsPath =
            path.EndsWith("/models", StringComparison.OrdinalIgnoreCase) ? path           // already the endpoint
            : path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? path + "/models" // versioned base
            : path + "/v1/models";

        return new UriBuilder(uri) { Path = modelsPath, Query = string.Empty, Fragment = string.Empty }.Uri.ToString();
    }

    private sealed record OpenAIModelsResponse
    {
        [JsonPropertyName("data")] public IReadOnlyList<OpenAIModel>? Data { get; init; }
    }

    private sealed record OpenAIModel
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
    }
}
