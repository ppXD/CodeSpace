using System.Net;
using System.Text;
using CodeSpace.Core.Services.Agents.ModelCredentials.Reflectors;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the OpenAI-compatible / LiteLLM reflector WIRE SHAPE without a live gateway: a captured
/// <see cref="HttpMessageHandler"/> asserts the request GETs <c>{baseUrl}/v1/models</c> with a bearer token, and the
/// response's <c>data[].id</c> ids become <see cref="ReflectedModel"/>s enriched from the in-code catalog. CanReflect
/// gates purely on a configured base URL — a direct vendor key (no base URL) is manual-only.
/// </summary>
[Trait("Category", "Unit")]
public class LiteLLMOpenAIReflectorTests
{
    [Theory]
    [InlineData("https://gateway.local/v1", true)]
    [InlineData("http://localhost:4000", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void CanReflect_is_true_only_when_a_base_url_is_configured(string? baseUrl, bool expected)
    {
        var reflector = new LiteLLMOpenAIReflector(new StubHttpClientFactory(new CapturingHandler("{}")));

        reflector.CanReflect(new ResolvedModelCredential { Provider = "Custom", ApiKey = "sk", BaseUrl = baseUrl }).ShouldBe(expected);
    }

    [Fact]
    public async Task Reflects_v1_models_and_enriches_known_ids_from_the_catalog()
    {
        const string response = """
            { "object": "list", "data": [ { "id": "gpt-5.4-codex" }, { "id": "my-co/custom-model" } ] }
            """;
        var reflector = new LiteLLMOpenAIReflector(new StubHttpClientFactory(new CapturingHandler(response)));

        var models = await reflector.ListModelsAsync(Credential("https://gateway.local"), CancellationToken.None);

        models.Select(m => m.ModelId).ShouldBe(new[] { "gpt-5.4-codex", "my-co/custom-model" });

        var codex = models.Single(m => m.ModelId == "gpt-5.4-codex");
        codex.Capabilities.SupportsToolUse.ShouldBeTrue("a known id is enriched from BuiltinModelCatalog");

        var custom = models.Single(m => m.ModelId == "my-co/custom-model");
        custom.Capabilities.SupportsToolUse.ShouldBeFalse("an unknown id gets the all-false floor");
    }

    [Fact]
    public async Task The_request_carries_a_bearer_token_on_the_named_redirect_disabled_client()
    {
        var handler = new CapturingHandler("""{ "data": [] }""");
        var factory = new StubHttpClientFactory(handler);

        await new LiteLLMOpenAIReflector(factory).ListModelsAsync(Credential("https://gateway.local", apiKey: "sk-secret"), CancellationToken.None);

        handler.AuthScheme.ShouldBe("Bearer");
        handler.AuthParameter.ShouldBe("sk-secret");
        factory.RequestedClientName.ShouldBe(LiteLLMOpenAIReflector.HttpClientName, "uses the named client carrying AllowAutoRedirect=false + the tight timeout, so the key can't leak via a downgrade redirect");
    }

    [Theory]
    [InlineData("https://gateway.local", "https://gateway.local/v1/models")]
    [InlineData("https://gateway.local/v1/", "https://gateway.local/v1/models")]          // trailing /v1 not doubled
    [InlineData("https://gateway.local/v1/models", "https://gateway.local/v1/models")]    // already the endpoint, not /models/v1/models
    [InlineData("https://gateway.local/v1?key=abc", "https://gateway.local/v1/models")]   // query stripped, not buried mid-path
    [InlineData("https://host/llm", "https://host/llm/v1/models")]                        // path prefix preserved
    public async Task The_models_url_is_built_robustly(string baseUrl, string expected)
    {
        var handler = new CapturingHandler("""{ "data": [] }""");

        await new LiteLLMOpenAIReflector(new StubHttpClientFactory(handler)).ListModelsAsync(Credential(baseUrl), CancellationToken.None);

        handler.RequestUri.ShouldBe(expected);
    }

    [Fact]
    public async Task An_empty_listing_yields_no_models()
    {
        var reflector = new LiteLLMOpenAIReflector(new StubHttpClientFactory(new CapturingHandler("""{ "object": "list", "data": [] }""")));

        (await reflector.ListModelsAsync(Credential("https://gateway.local"), CancellationToken.None)).ShouldBeEmpty();
    }

    private static ResolvedModelCredential Credential(string baseUrl, string? apiKey = "sk") =>
        new() { Provider = "Custom", ApiKey = apiKey, BaseUrl = baseUrl };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestUri;
        public string? AuthScheme;
        public string? AuthParameter;
        private readonly string _responseJson;

        public CapturingHandler(string responseJson) { _responseJson = responseJson; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            AuthScheme = request.Headers.Authorization?.Scheme;
            AuthParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_responseJson, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public string? RequestedClientName;
        public StubHttpClientFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) { RequestedClientName = name; return new(_handler, disposeHandler: false); }
    }
}
