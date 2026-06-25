using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Custom;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the Custom in-process client: it is a <see cref="IStructuredLLMClient"/> tagged <c>"Custom"</c> (so the registry
/// resolves it for a Custom credential), and it speaks the OpenAI-compatible WIRE to the credential's OWN base URL +
/// key — proving a Custom-tagged gateway model drives the in-process plane (supervisor / planner / effort), not just the
/// agent harness. The wire shape itself is pinned by <c>OpenAiClientStructuredTests</c>; this only proves the
/// re-tag-and-delegate.
/// </summary>
[Trait("Category", "Unit")]
public class CustomClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? AuthorizationHeader;
        public string? RequestUri;
        public HttpStatusCode Status = HttpStatusCode.OK;
        private readonly string _responseJson;
        public CapturingHandler(string responseJson) { _responseJson = responseJson; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationHeader = request.Headers.Authorization?.ToString();
            RequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(_responseJson, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    [Fact]
    public void It_is_a_structured_client_tagged_custom()
    {
        var client = new CustomClient(new StubHttpClientFactory(new CapturingHandler("{}")));

        client.Provider.ShouldBe("Custom", "the registry resolves THIS client for a credential whose Provider is 'Custom'");
        (client is IStructuredLLMClient).ShouldBeTrue("Custom must be a structured provider so it can run the supervisor brain / planner / effort");
    }

    [Fact]
    public void The_module_registers_the_custom_client_under_the_custom_tag()
    {
        var module = new CustomLlmProviderModule();

        module.Provider.ShouldBe("Custom");
        module.Client.ShouldBe(typeof(CustomClient), "the DI scan wires this client as the Custom provider's ILLMClient + IStructuredLLMClient");
    }

    [Fact]
    public async Task It_speaks_the_OpenAI_wire_to_the_credentials_own_base_url_and_key()
    {
        // A forced-function OpenAI-shaped reply; the Custom client delegates to the OpenAI wire and extracts the JSON.
        const string response = """
            { "model": "metis-coder-max", "choices": [ { "message": { "role": "assistant", "content": null,
              "tool_calls": [ { "id": "c1", "type": "function", "function": { "name": "respond", "arguments": "{\"ok\":true}" } } ] }, "finish_reason": "tool_calls" } ] }
            """;
        var handler = new CapturingHandler(response);
        var client = new CustomClient(new StubHttpClientFactory(handler));

        var credential = new ResolvedModelCredential { Provider = "Custom", ApiKey = "gw-key", BaseUrl = "https://my-gateway.local/v1" };
        var request = new StructuredLLMCompletionRequest
        {
            Model = "metis-coder-max", SystemPrompt = "sys", UserPrompt = "go", Credential = credential,
            JsonSchema = JsonDocument.Parse("""{ "type": "object", "properties": { "ok": { "type": "boolean" } } }""").RootElement,
        };

        var result = await client.CompleteStructuredAsync(request, CancellationToken.None);

        result.Json.GetProperty("ok").GetBoolean().ShouldBeTrue("the Custom client parses the OpenAI-wire structured reply");
        handler.RequestUri.ShouldBe("https://my-gateway.local/v1/chat/completions", "the request hits the credential's OWN custom endpoint, not api.openai.com");
        handler.AuthorizationHeader.ShouldBe("Bearer gw-key", "the credential's key authenticates the custom gateway");
    }

    [Fact]
    public async Task A_transport_error_is_re_tagged_to_the_custom_provider_preserving_the_category()
    {
        // A misconfigured custom gateway returns 401 → the wire raises an AuthFailed LlmApiException tagged "OpenAI";
        // the Custom client re-tags it "Custom" so the operator sees their OWN provider, with the category preserved
        // (so the decider's capability-miss filter still works).
        var handler = new CapturingHandler("""{ "error": { "message": "bad key" } }""") { Status = HttpStatusCode.Unauthorized };
        var client = new CustomClient(new StubHttpClientFactory(handler));

        var request = new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u",
            Credential = new ResolvedModelCredential { Provider = "Custom", ApiKey = "k", BaseUrl = "https://gw.local/v1" },
            JsonSchema = JsonDocument.Parse("""{ "type": "object" }""").RootElement,
        };

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(request, CancellationToken.None));

        ex.Provider.ShouldBe("Custom", "the error names the operator's Custom gateway, not the underlying OpenAI wire");
        ex.Category.ShouldBe(LlmErrorCategory.AuthFailed, "the typed category is preserved so the decider's capability-miss filter is unaffected");
        ex.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task A_keyless_Custom_gateway_runs_with_no_authorization_header()
    {
        // The headline keyless case: a Custom credential with a base URL but NO key (a local / no-auth gateway) drives the
        // in-process plane with no Authorization header — Custom endpoints run to the supervisor even keyless.
        const string response = """
            { "model": "m", "choices": [ { "message": { "role": "assistant", "content": null,
              "tool_calls": [ { "id": "c", "type": "function", "function": { "name": "respond", "arguments": "{\"ok\":true}" } } ] }, "finish_reason": "tool_calls" } ] }
            """;
        var handler = new CapturingHandler(response);
        var client = new CustomClient(new StubHttpClientFactory(handler));

        var keyless = new ResolvedModelCredential { Provider = "Custom", ApiKey = null, BaseUrl = "https://local-gw.local/v1" };
        var request = new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = keyless,
            JsonSchema = JsonDocument.Parse("""{ "type": "object" }""").RootElement,
        };

        var result = await client.CompleteStructuredAsync(request, CancellationToken.None);

        result.Json.GetProperty("ok").GetBoolean().ShouldBeTrue("a keyless Custom gateway runs the in-process structured call");
        handler.AuthorizationHeader.ShouldBeNull("no key → NO Authorization header to the keyless gateway");
        handler.RequestUri.ShouldBe("https://local-gw.local/v1/chat/completions");
    }
}
