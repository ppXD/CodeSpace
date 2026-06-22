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
}
