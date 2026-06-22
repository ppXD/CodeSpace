using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the engine's failure classification (PR-2): a thrown <c>IRetryClassifiedException</c> (an LLM auth / rate-limit
/// / transient fault) decides retry-vs-fail-fast, so the engine never burns every attempt with backoff on a terminal
/// auth error, and honors a provider Retry-After. Any other failure stays retryable (the prior status-blind default).
/// </summary>
[Trait("Category", "Unit")]
public class RetryClassificationTests
{
    [Fact]
    public void A_non_retryable_typed_fault_fails_fast()
    {
        var (retryable, retryAfter) = WorkflowEngine.ClassifyFailure(
            new LlmApiException("Anthropic", 401, LlmErrorCategory.AuthFailed, "bad key"));

        retryable.ShouldBeFalse("an auth failure must not be retried — it would burn every attempt");
        retryAfter.ShouldBeNull();
    }

    [Fact]
    public void A_retryable_typed_fault_retries_and_carries_the_retry_after()
    {
        var (retryable, retryAfter) = WorkflowEngine.ClassifyFailure(
            new LlmApiException("OpenAI", 429, LlmErrorCategory.RateLimited, "slow down", retryAfter: TimeSpan.FromSeconds(5)));

        retryable.ShouldBeTrue();
        retryAfter.ShouldBe(TimeSpan.FromSeconds(5), "a 429's Retry-After becomes the next attempt's backoff");
    }

    [Fact]
    public void A_typed_fault_nested_in_an_inner_exception_is_still_found()
    {
        // The engine wraps / the supervisor decider may rethrow — the classification walks the InnerException chain.
        var wrapped = new InvalidOperationException("turn failed",
            new LlmApiException("Anthropic", 503, LlmErrorCategory.Transient, "unavailable"));

        WorkflowEngine.ClassifyFailure(wrapped).Retryable.ShouldBeTrue("a transient fault wrapped in an outer exception is still retryable");
    }

    [Fact]
    public void An_untyped_failure_stays_retryable_preserving_the_prior_default()
    {
        WorkflowEngine.ClassifyFailure(new InvalidOperationException("generic node failure")).Retryable.ShouldBeTrue();
        WorkflowEngine.ClassifyFailure(null).Retryable.ShouldBeTrue("a node-returned Fail with no thrown exception retries per the plan, unchanged");
    }
}
