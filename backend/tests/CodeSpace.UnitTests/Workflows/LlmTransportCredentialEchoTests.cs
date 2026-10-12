using System.Net;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// A gateway that ECHOES the credential it was presented — a 401 quoting the key it rejected, a 429/5xx from a proxy that
/// reflects request headers and URL — must never get that credential into the typed failure. The failure's message is
/// what the engine writes to node / interaction / external-call failure records and what a Viewer reads on the run, so
/// the transport strikes every value the request presented before the exception exists. Both wires, buffered and
/// streamed; the classification (status, category, Retry-After) is read from the raw body and is unchanged.
/// </summary>
[Trait("Category", "Unit")]
public class LlmTransportCredentialEchoTests
{
    private const string Key = "sk-ECHO-CANARY-7c1d0e9f8a2b";

    public enum Wire { OpenAi, Anthropic }

    [Theory]
    [InlineData(Wire.OpenAi, HttpStatusCode.Unauthorized, LlmErrorCategory.AuthFailed)]
    [InlineData(Wire.OpenAi, HttpStatusCode.TooManyRequests, LlmErrorCategory.RateLimited)]
    [InlineData(Wire.OpenAi, HttpStatusCode.InternalServerError, LlmErrorCategory.Transient)]
    [InlineData(Wire.Anthropic, HttpStatusCode.Unauthorized, LlmErrorCategory.AuthFailed)]
    [InlineData(Wire.Anthropic, HttpStatusCode.TooManyRequests, LlmErrorCategory.RateLimited)]
    [InlineData(Wire.Anthropic, HttpStatusCode.BadGateway, LlmErrorCategory.Transient)]
    public async Task A_buffered_call_never_carries_an_echoed_key_into_the_failure(Wire wire, HttpStatusCode status, LlmErrorCategory expected)
    {
        var handler = new EchoingHandler(status);

        var ex = await Should.ThrowAsync<LlmApiException>(() => Client(wire, handler).CompleteAsync(Request(wire, "https://gw.example/v1"), CancellationToken.None));

        handler.Echoed.ShouldContain(Key, Case.Sensitive, "fixture check: the gateway really echoed the presented key");
        ShouldCarryNoKey(ex, (int)status, expected);
    }

    [Theory]
    [InlineData(Wire.OpenAi, HttpStatusCode.Unauthorized, LlmErrorCategory.AuthFailed)]
    [InlineData(Wire.OpenAi, HttpStatusCode.TooManyRequests, LlmErrorCategory.RateLimited)]
    [InlineData(Wire.OpenAi, HttpStatusCode.ServiceUnavailable, LlmErrorCategory.Transient)]
    [InlineData(Wire.Anthropic, HttpStatusCode.Forbidden, LlmErrorCategory.AuthFailed)]
    [InlineData(Wire.Anthropic, HttpStatusCode.TooManyRequests, LlmErrorCategory.RateLimited)]
    [InlineData(Wire.Anthropic, HttpStatusCode.InternalServerError, LlmErrorCategory.Transient)]
    public async Task A_streamed_call_never_carries_an_echoed_key_into_the_failure(Wire wire, HttpStatusCode status, LlmErrorCategory expected)
    {
        var handler = new EchoingHandler(status);
        var streaming = (IStreamingLLMClient)Client(wire, handler);

        var ex = await Should.ThrowAsync<LlmApiException>(async () =>
        {
            await foreach (var _ in streaming.StreamAsync(Request(wire, "https://gw.example/v1"), CancellationToken.None)) { }
        });

        handler.Echoed.ShouldContain(Key, Case.Sensitive, "fixture check: the gateway really echoed the presented key");
        ShouldCarryNoKey(ex, (int)status, expected);
    }

    [Fact]
    public async Task Credentials_embedded_in_the_base_url_are_struck_from_an_echo_of_the_request_url()
    {
        const string password = "pw-ECHO-CANARY-2f3e4d5c";
        var handler = new EchoingHandler(HttpStatusCode.Unauthorized);

        var ex = await Should.ThrowAsync<LlmApiException>(() => Client(Wire.Anthropic, handler).CompleteAsync(Request(Wire.Anthropic, $"https://svc:{password}@gw.example"), CancellationToken.None));

        handler.Echoed.ShouldContain(password, Case.Sensitive, "fixture check: the echoed request URL carried the userinfo");
        ex.Message.ShouldNotContain(password);
        ex.ProviderMessage.ShouldNotContain(password);
        ex.Message.ShouldContain("gw.example", Case.Sensitive, "the host stays readable — it says which endpoint answered");
    }

    [Fact]
    public async Task A_body_with_no_echo_reaches_the_failure_verbatim()
    {
        var handler = new EchoingHandler(HttpStatusCode.Unauthorized) { EchoCredential = false };

        var ex = await Should.ThrowAsync<LlmApiException>(() => Client(Wire.OpenAi, handler).CompleteAsync(Request(Wire.OpenAi, "https://gw.example/v1"), CancellationToken.None));

        ex.ProviderMessage.ShouldBe(handler.Echoed, "nothing to strike — the operator reads the gateway's own words");
    }

    private static void ShouldCarryNoKey(LlmApiException ex, int status, LlmErrorCategory expected)
    {
        ex.Message.ShouldNotContain(Key);
        ex.ProviderMessage.ShouldNotContain(Key);
        ex.ProviderMessage.ShouldContain("***", Case.Sensitive, "the echo is struck in place, so the rest of the gateway's sentence stays legible");
        ex.StatusCode.ShouldBe(status);
        ex.Category.ShouldBe(expected, "the category is read from the raw body — striking the echo cannot change it");
    }

    private static ILLMClient Client(Wire wire, HttpMessageHandler handler) => wire == Wire.OpenAi ? new OpenAiClient(new StubFactory(handler)) : new AnthropicClient(new StubFactory(handler));

    private static LLMCompletionRequest Request(Wire wire, string baseUrl) => new()
    {
        Model = "m", SystemPrompt = "s", UserPrompt = "u",
        Credential = new ResolvedModelCredential { Provider = wire == Wire.OpenAi ? "OpenAI" : "Anthropic", ApiKey = Key, BaseUrl = baseUrl },
    };

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Answers every request with <paramref name="status"/> and a JSON error quoting the request's credential headers and URL — what a reflecting proxy or a careless gateway sends.</summary>
    private sealed class EchoingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public bool EchoCredential { get; init; } = true;

        public string Echoed { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var presented = request.Headers.Authorization?.ToString() ?? string.Join(",", request.Headers.TryGetValues("x-api-key", out var values) ? values : []);
            var message = EchoCredential ? $"Invalid API key provided: {presented} (request {request.RequestUri!.OriginalString})" : "Invalid API key provided.";

            Echoed = JsonSerializer.Serialize(new { error = new { message, type = "invalid_request_error" } });

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(Echoed, Encoding.UTF8, "application/json") });
        }
    }
}
