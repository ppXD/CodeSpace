using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the TYPED transport contract (PR-1): a non-2xx becomes a machine-actionable <see cref="LlmApiException"/>
/// (status + category + Retry-After), a client-side timeout becomes a <see cref="LlmErrorCategory.Transient"/> while an
/// operator cancel propagates, and the structured degrade fires ONLY on a 400-shape rejection — a 401/429/5xx on the
/// forced-tool attempt PROPAGATES instead of being swallowed into a second billable call. Covers both wires.
/// </summary>
[Trait("Category", "Unit")]
public class LlmTransportTypedErrorTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;
    private static readonly ResolvedModelCredential AnthropicCred = new() { Provider = "Anthropic", ApiKey = "k" };
    private static readonly ResolvedModelCredential OpenAiCred = new() { Provider = "OpenAI", ApiKey = "k" };

    // ── Anthropic ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmErrorCategory.AuthFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmErrorCategory.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, LlmErrorCategory.Transient)]
    [InlineData(HttpStatusCode.BadGateway, LlmErrorCategory.Transient)]
    public async Task Anthropic_plain_completion_throws_a_typed_exception_on_a_non_2xx(HttpStatusCode status, LlmErrorCategory expected)
    {
        var client = new AnthropicClient(Factory(new SequencedHandler((status, """{"error":{"message":"boom"}}"""))));

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = AnthropicCred,
        }, CancellationToken.None));

        ex.Category.ShouldBe(expected);
        ex.StatusCode.ShouldBe((int)status);
        ex.Provider.ShouldBe("Anthropic");
        ex.ProviderMessage.ShouldContain("boom");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmErrorCategory.AuthFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmErrorCategory.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LlmErrorCategory.Transient)]
    public async Task Anthropic_structured_PROPAGATES_a_non_400_on_the_forced_tool_attempt_without_degrading(HttpStatusCode status, LlmErrorCategory expected)
    {
        // The hazard PR-1 closes: a 401/429/5xx on attempt 1 must NOT be swallowed as "degrade to floor" — that would
        // issue a SECOND billable call and erase the real auth/rate cause. Exactly ONE request must hit the endpoint.
        var handler = new SequencedHandler((status, """{"error":{"message":"nope"}}"""));
        var client = new AnthropicClient(Factory(handler));

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = Schema, Credential = AnthropicCred,
        }, CancellationToken.None));

        ex.Category.ShouldBe(expected);
        handler.Count.ShouldBe(1, "a non-400 must propagate from attempt 1 — NOT trigger the prompt-only floor (a second billable call)");
    }

    [Fact]
    public async Task Anthropic_structured_DEGRADES_on_a_400_forced_tool_rejection()
    {
        // A 400 (feature unsupported) is the ONE category that degrades — attempt 2 (prompt-only) recovers the JSON.
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{"error":{"message":"tool_choice unsupported"}}"""),
            (HttpStatusCode.OK, """{"model":"m","content":[{"type":"text","text":"{\"kind\":\"plan\"}"}]}"""));
        var client = new AnthropicClient(Factory(handler));

        var result = await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = Schema, Credential = AnthropicCred,
        }, CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("plan");
        handler.Count.ShouldBe(2, "a 400 degrades to the prompt-only floor");
    }

    [Theory]
    [InlineData("the 'tool name' is too long for this model")]   // 'too long' must NOT be read as a context-window overflow
    [InlineData("forced tool-use violates the safety_settings of this gateway")]   // 'safety' must NOT be read as a content block
    public async Task A_400_whose_body_trips_a_classification_keyword_still_degrades(string rejectionBody)
    {
        // The regression the review caught: the degrade must key on the STATUS (a 400/422 request-shape rejection), not
        // the refined category — a feature-unsupported 400 whose body happens to contain 'too long' / 'safety' must still
        // fall back to the prompt-only floor, never propagate and fail the structured run.
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"" + rejectionBody + "\"}}"),
            (HttpStatusCode.OK, """{"model":"m","content":[{"type":"text","text":"{\"kind\":\"plan\"}"}]}"""));
        var client = new AnthropicClient(Factory(handler));

        var result = await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = Schema, Credential = AnthropicCred,
        }, CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("plan");
        handler.Count.ShouldBe(2, "a 400 must degrade regardless of a body keyword — the fallback can't be disabled by prose");
    }

    [Fact]
    public async Task A_404_does_NOT_degrade_and_fails_fast()
    {
        // A 404 (wrong model id / misconfigured base URL) is a routing error — it must propagate after ONE call, not
        // burn a second billable prompt-only request against the same wrong endpoint.
        var handler = new SequencedHandler((HttpStatusCode.NotFound, """{"error":"model not found"}"""));
        var client = new AnthropicClient(Factory(handler));

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "ghost", SystemPrompt = "s", UserPrompt = "u", JsonSchema = Schema, Credential = AnthropicCred,
        }, CancellationToken.None));

        ex.StatusCode.ShouldBe(404);
        handler.Count.ShouldBe(1, "a 404 routing error must fail fast — never degrade to a second call against the same wrong endpoint");
    }

    [Fact]
    public async Task A_rate_limit_carries_the_parsed_retry_after()
    {
        var handler = new SequencedHandler((HttpStatusCode.TooManyRequests, """{"error":"slow down"}""")) { RetryAfterSeconds = 12 };
        var client = new AnthropicClient(Factory(handler));

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = AnthropicCred,
        }, CancellationToken.None));

        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task A_client_side_timeout_becomes_transient_but_an_operator_cancel_propagates()
    {
        // A handler that throws TaskCanceledException — once with the token NOT cancelled (a HttpClient.Timeout), once
        // with it cancelled (an operator/run cancel). The first must become a retryable Transient; the second must
        // propagate as OperationCanceledException so the run tears down, never mislabelled as a gateway fault.
        var timeoutClient = new AnthropicClient(Factory(new ThrowingHandler(cancelToken: false)));
        var ex = await Should.ThrowAsync<LlmApiException>(() => timeoutClient.CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = AnthropicCred,
        }, CancellationToken.None));
        ex.Category.ShouldBe(LlmErrorCategory.Transient);
        ex.StatusCode.ShouldBeNull();

        using var cts = new CancellationTokenSource();
        var cancelClient = new AnthropicClient(Factory(new ThrowingHandler(cancelToken: true, cts)));
        await Should.ThrowAsync<OperationCanceledException>(() => cancelClient.CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = AnthropicCred,
        }, cts.Token));
    }

    // ── OpenAI ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmErrorCategory.AuthFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmErrorCategory.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LlmErrorCategory.Transient)]
    public async Task OpenAi_structured_PROPAGATES_a_non_400_on_the_forced_function_attempt(HttpStatusCode status, LlmErrorCategory expected)
    {
        var handler = new SequencedHandler((status, """{"error":{"message":"nope"}}"""));
        var client = new OpenAiClient(Factory(handler));

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = Schema, Credential = OpenAiCred,
        }, CancellationToken.None));

        ex.Category.ShouldBe(expected);
        handler.Count.ShouldBe(1, "a non-400 propagates — no second billable floor call");
    }

    [Fact]
    public async Task OpenAi_structured_DEGRADES_on_a_400_forced_function_rejection()
    {
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{"error":{"message":"functions unsupported"}}"""),
            (HttpStatusCode.OK, """{"model":"m","choices":[{"message":{"content":"{\"kind\":\"merge\"}"}}]}"""));
        var client = new OpenAiClient(Factory(handler));

        var result = await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = Schema, Credential = OpenAiCred,
        }, CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("merge");
        handler.Count.ShouldBe(2);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────

    private static IHttpClientFactory Factory(HttpMessageHandler handler) => new StubFactory(handler);

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
        public int Count { get; private set; }
        public int? RetryAfterSeconds { get; init; }
        public SequencedHandler(params (HttpStatusCode, string)[] responses) { _responses = new(responses); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (RetryAfterSeconds is { } s) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly bool _cancelToken;
        private readonly CancellationTokenSource? _cts;
        public ThrowingHandler(bool cancelToken, CancellationTokenSource? cts = null) { _cancelToken = cancelToken; _cts = cts; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_cancelToken) _cts!.Cancel();   // simulate an operator cancel: the token is now cancelled when the OCE flies
            throw new TaskCanceledException("simulated cancellation");
        }
    }
}
