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
/// with the primary handler overridden by a fake, so a transient 5xx OR a thrown <see cref="HttpRequestException"/> is
/// transparently retried while a terminal 4xx is fed straight through (no wasted, billable retry). A thrown
/// <see cref="HttpRequestException"/> is ambiguous (the reset may have followed a billed send), so a status failure
/// that reaches the caller AFTER one rides <see cref="LlmApiException.PriorBilledAttempt"/> — see
/// <see cref="LlmBudgetGuard.ObservedNoSpend"/> for why that must hold the budget reservation rather than release it.
/// This is the registration the live plane uses; the test reuses the SAME extension, not a re-derived copy.
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
    public async Task A_thrown_HttpRequestException_IS_retried_to_success()
    {
        // THE new truth (reverted from 1e98cee4b's narrowing): ShouldHandle is Polly's DEFAULT again, so a thrown
        // HttpRequestException is retried at this layer exactly like a transient status, instead of surfacing
        // immediately after one attempt.
        var handler = new ThrowFirstThenSequencedHandler(new HttpRequestException("connection reset"),
            (HttpStatusCode.OK, """{"model":"m","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":1,"output_tokens":1}}"""));

        var result = await BuildClient(handler).CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = Cred,
        }, CancellationToken.None);

        result.Text.ShouldBe("hi");
        handler.Count.ShouldBe(2, "the resilience handler retried the thrown exception once, then the 200 succeeded");
    }

    [Fact]
    public async Task A_status_failure_after_a_thrown_HttpRequestException_carries_PriorBilledAttempt()
    {
        // The ambiguity does not disappear just because a LATER attempt got a real response: attempt 1 was sent and
        // lost its reply before attempt 2/3 ever answered, so it may already have been billed. PhysicalLlmAccountingHandler
        // marks that on the shared (Polly-reused) request, and LlmHttpTransport carries the mark onto the exception it
        // builds for the LAST attempt's own — otherwise clean — status failure, so LlmBudgetGuard.ObservedNoSpend
        // (mutation: not marking → false → red) holds the reservation instead of releasing it.
        var handler = new ThrowFirstThenSequencedHandler(new HttpRequestException("connection reset"),
            (HttpStatusCode.ServiceUnavailable, """{"error":"still warming up"}"""),
            (HttpStatusCode.ServiceUnavailable, """{"error":"still warming up"}"""));

        var ex = await Should.ThrowAsync<LlmApiException>(() => BuildClient(handler).CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = Cred,
        }, CancellationToken.None));

        ex.StatusCode.ShouldBe(503);
        ex.PriorBilledAttempt.ShouldBeTrue("attempt 1 was sent and its response was lost — the reservation must not be released on the strength of a later attempt's own clean status");
        handler.Count.ShouldBe(3, "MaxRetryAttempts=2 retries the ambiguous throw, then retries the still-transient 503 once more before giving up");
    }

    [Fact]
    public async Task A_503_with_no_earlier_sent_attempt_has_no_PriorBilledAttempt_and_still_releases()
    {
        // A clean transient status failure with NOTHING ambiguous behind it must still prove unbilled — the flag must
        // default to false, not be set merely because the call was retried.
        var handler = new SequencedHandler(
            (HttpStatusCode.ServiceUnavailable, """{"error":"warming up"}"""),
            (HttpStatusCode.ServiceUnavailable, """{"error":"warming up"}"""),
            (HttpStatusCode.ServiceUnavailable, """{"error":"warming up"}"""));

        var ex = await Should.ThrowAsync<LlmApiException>(() => BuildClient(handler).CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = Cred,
        }, CancellationToken.None));

        ex.StatusCode.ShouldBe(503);
        ex.PriorBilledAttempt.ShouldBeFalse("every attempt got its own clean response — nothing here rode over a lost earlier one");
        handler.Count.ShouldBe(3, "MaxRetryAttempts=2 exhausts on the third still-transient 503");
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

    /// <summary>Throws <paramref name="fault"/> on the FIRST attempt only, then serves <paramref name="thenReplies"/> in order (repeating the last one if retries outrun the queue) — the shape of a reset-after-send whose retry then gets its own real response.</summary>
    private sealed class ThrowFirstThenSequencedHandler : HttpMessageHandler
    {
        private readonly Exception _fault;
        private readonly Queue<(HttpStatusCode Status, string Body)> _thenReplies;
        public int Count { get; private set; }
        public ThrowFirstThenSequencedHandler(Exception fault, params (HttpStatusCode, string)[] thenReplies) { _fault = fault; _thenReplies = new(thenReplies); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            if (Count == 1) throw _fault;

            var (status, body) = _thenReplies.Count > 1 ? _thenReplies.Dequeue() : _thenReplies.Peek();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
