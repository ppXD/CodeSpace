using System.Net;
using System.Text;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Proves the PRODUCTION registration (<see cref="LlmHttpClientRegistration.AddLlmHttpClients"/>) actually wires the
/// retry-only resilience pipeline onto the named LLM clients — driven through a real <see cref="IHttpClientFactory"/>
/// with the primary handler overridden by a fake, so a transient 5xx is transparently retried to success while a
/// terminal 4xx is fed straight through (no wasted, billable retry). This is the registration the live plane uses; the
/// test reuses the SAME extension, not a re-derived copy.
/// </summary>
[Trait("Category", "Unit")]
public class LlmTransportResilienceTests
{
    private static readonly ResolvedModelCredential Cred = new() { Provider = "Anthropic", ApiKey = "k" };

    [Fact]
    public async Task A_transient_503_is_retried_to_success()
    {
        var handler = new SequencedHandler(
            (HttpStatusCode.ServiceUnavailable, """{"error":"warming up"}"""),
            (HttpStatusCode.OK, """{"model":"m","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":1,"output_tokens":1}}"""));

        var client = BuildClient(handler);

        var result = await client.CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = Cred,
        }, CancellationToken.None);

        result.Text.ShouldBe("hi");
        handler.Count.ShouldBe(2, "the resilience handler retried the 503 once, then the 200 succeeded");
    }

    [Fact]
    public async Task A_terminal_400_is_NOT_retried()
    {
        // A 400 is not transient — retrying would waste time + re-bill. It must surface immediately as a typed BadRequest
        // after exactly ONE request.
        var handler = new SequencedHandler((HttpStatusCode.BadRequest, """{"error":"bad model"}"""));
        var client = BuildClient(handler);

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = Cred,
        }, CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.BadRequest);
        handler.Count.ShouldBe(1, "a 400 is terminal — the retry strategy must not re-attempt it");
    }

    [Fact]
    public async Task A_thrown_HttpRequestException_is_NOT_retried_by_this_layer()
    {
        // THE fix: ShouldHandle is STATUS-ONLY. Before it, Polly's DEFAULT predicate also retried
        // HttpRequestException — so a reset-after-send attempt (possibly already billed) got a second, billable
        // physical request under the SAME budget reservation before the caller ever saw a failure. Now the
        // exception must reach LlmHttpTransport (which wraps it into a status-less Transient LlmApiException) after
        // exactly ONE attempt; the engine-level RetryPlan is the layer that re-attempts, with its own reservation.
        var handler = new ThrowingHandler(new HttpRequestException("connection reset"));
        var client = BuildClient(handler);

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = Cred,
        }, CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.Transient);
        handler.Count.ShouldBe(1, "a transport exception must not be retried at this layer — retrying it would launder a possibly-billed attempt into a released reservation");
    }

    /// <summary>Build an AnthropicClient over the REAL named-client registration, with the primary handler swapped for the fake (the resilience delegating handler stays in the pipeline).</summary>
    private static AnthropicClient BuildClient(HttpMessageHandler fake)
    {
        var services = new ServiceCollection();
        services.AddLlmHttpClients();
        services.AddHttpClient(nameof(AnthropicClient)).ConfigurePrimaryHttpMessageHandler(() => fake);

        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new AnthropicClient(factory);
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
        public int Count { get; private set; }
        public SequencedHandler(params (HttpStatusCode, string)[] responses) { _responses = new(responses); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _fault;
        public int Count { get; private set; }
        public ThrowingHandler(Exception fault) { _fault = fault; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            throw _fault;
        }
    }
}
