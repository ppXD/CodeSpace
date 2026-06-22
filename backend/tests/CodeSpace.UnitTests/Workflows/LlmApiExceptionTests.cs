using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the LLM error TAXONOMY — the machine-actionable classification the structured-degrade, the engine RetryPlan,
/// and the decider branch on. A drift here (e.g. a 429 mis-classed as terminal, or a 401 mis-classed as retryable)
/// would silently break retry-vs-fail decisions across the whole LLM plane.
/// </summary>
[Trait("Category", "Unit")]
public class LlmApiExceptionTests
{
    [Theory]
    [InlineData(401, LlmErrorCategory.AuthFailed)]
    [InlineData(403, LlmErrorCategory.AuthFailed)]
    [InlineData(429, LlmErrorCategory.RateLimited)]
    [InlineData(408, LlmErrorCategory.Transient)]
    [InlineData(500, LlmErrorCategory.Transient)]
    [InlineData(502, LlmErrorCategory.Transient)]
    [InlineData(503, LlmErrorCategory.Transient)]
    [InlineData(400, LlmErrorCategory.BadRequest)]
    [InlineData(422, LlmErrorCategory.BadRequest)]
    [InlineData(404, LlmErrorCategory.BadRequest)]
    public void Classify_maps_status_to_category(int status, LlmErrorCategory expected)
    {
        LlmApiException.Classify(status, body: null).ShouldBe(expected);
    }

    [Theory]
    [InlineData(400, "This model's maximum context length is 8192 tokens")]
    [InlineData(413, "input is too long for the context window")]
    [InlineData(422, "reduce the length of the messages")]
    public void Classify_refines_a_4xx_to_context_length_on_a_matching_body(int status, string body)
    {
        LlmApiException.Classify(status, body).ShouldBe(LlmErrorCategory.ContextLengthExceeded);
    }

    [Theory]
    [InlineData("blocked by the content filter")]
    [InlineData("violates our content policy")]
    [InlineData("the response was flagged for safety")]
    public void Classify_refines_a_400_to_content_filtered_on_a_matching_body(string body)
    {
        LlmApiException.Classify(400, body).ShouldBe(LlmErrorCategory.ContentFiltered);
    }

    [Fact]
    public void A_context_or_filter_status_that_is_5xx_or_429_stays_transient_or_rate_limited()
    {
        // The body keyword refinement applies ONLY to a 4xx — a 503 whose body happens to mention "safety" is still a
        // transient outage to RETRY, not a terminal content block.
        LlmApiException.Classify(503, "temporary safety system outage").ShouldBe(LlmErrorCategory.Transient);
        LlmApiException.Classify(429, "rate limited by the content policy service").ShouldBe(LlmErrorCategory.RateLimited);
    }

    [Theory]
    [InlineData(LlmErrorCategory.Transient, true)]
    [InlineData(LlmErrorCategory.RateLimited, true)]
    [InlineData(LlmErrorCategory.AuthFailed, false)]
    [InlineData(LlmErrorCategory.BadRequest, false)]
    [InlineData(LlmErrorCategory.ContextLengthExceeded, false)]
    [InlineData(LlmErrorCategory.ContentFiltered, false)]
    [InlineData(LlmErrorCategory.Malformed, false)]
    public void IsRetryable_is_true_only_for_transient_and_rate_limited(LlmErrorCategory category, bool retryable)
    {
        new LlmApiException("X", 0, category, "msg").IsRetryable.ShouldBe(retryable);
    }

    [Fact]
    public void The_exception_preserves_status_category_message_and_retry_after()
    {
        var ex = new LlmApiException("Anthropic", 429, LlmErrorCategory.RateLimited, "slow down", retryAfter: TimeSpan.FromSeconds(7));

        ex.Provider.ShouldBe("Anthropic");
        ex.StatusCode.ShouldBe(429);
        ex.Category.ShouldBe(LlmErrorCategory.RateLimited);
        ex.ProviderMessage.ShouldBe("slow down");
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(7));
        ex.Message.ShouldContain("HTTP 429");
        ex.Message.ShouldContain("RateLimited");
        ex.Message.ShouldContain("slow down");
    }

    [Fact]
    public void A_transport_failure_with_no_response_carries_a_null_status()
    {
        var ex = new LlmApiException("OpenAI", statusCode: null, LlmErrorCategory.Transient, "the request timed out");

        ex.StatusCode.ShouldBeNull();
        ex.Message.ShouldContain("no-status");
        ex.IsRetryable.ShouldBeTrue();
    }
}
