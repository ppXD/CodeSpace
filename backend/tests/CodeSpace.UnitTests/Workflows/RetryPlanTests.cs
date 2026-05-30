using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="RetryPlan"/> is the clamp boundary between an operator-authored
/// <see cref="RetryPolicy"/> (any value) and the engine's bounded retry loop. These pin that a
/// null policy is a single attempt (the non-breaking default), and that attempts + backoff are
/// always clamped into range — even for a definition that bypassed save-time validation.
/// </summary>
[Trait("Category", "Unit")]
public class RetryPlanTests
{
    [Fact]
    public void Null_policy_is_a_single_attempt_with_no_backoff()
    {
        var plan = RetryPlan.From(null);

        plan.MaxAttempts.ShouldBe(1);
        plan.BackoffSeconds.ShouldBe(0);
        plan.RetriesOnFailure.ShouldBeFalse("one attempt is exactly the pre-retry behaviour");
        plan.Delay.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0, 1)]                                                  // floor: 0 → 1
    [InlineData(-5, 1)]                                                 // negative → 1
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(RetryPlan.MaxAttemptsCap, RetryPlan.MaxAttemptsCap)]    // at cap
    [InlineData(RetryPlan.MaxAttemptsCap + 1, RetryPlan.MaxAttemptsCap)]// above cap → clamped down
    [InlineData(1000, RetryPlan.MaxAttemptsCap)]
    public void Clamps_max_attempts_into_range(int requested, int expected)
    {
        RetryPlan.From(new RetryPolicy { MaxAttempts = requested }).MaxAttempts.ShouldBe(expected);
    }

    [Theory]
    [InlineData(-1, 0)]                                                       // negative → 0
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(RetryPlan.MaxBackoffSeconds, RetryPlan.MaxBackoffSeconds)]    // at cap
    [InlineData(RetryPlan.MaxBackoffSeconds + 1, RetryPlan.MaxBackoffSeconds)]// above cap → clamped down
    [InlineData(99999, RetryPlan.MaxBackoffSeconds)]
    public void Clamps_backoff_into_range(double requested, double expected)
    {
        RetryPlan.From(new RetryPolicy { MaxAttempts = 3, BackoffSeconds = requested }).BackoffSeconds.ShouldBe(expected);
    }

    [Fact]
    public void NaN_backoff_collapses_to_zero()
    {
        // double.NaN fails every comparison, so the "> 0 and <= cap" gate must fall through to 0
        // rather than propagating NaN into a Task.Delay (which would throw at run time).
        RetryPlan.From(new RetryPolicy { MaxAttempts = 2, BackoffSeconds = double.NaN }).BackoffSeconds.ShouldBe(0);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(5, true)]
    public void RetriesOnFailure_reflects_more_than_one_attempt(int maxAttempts, bool expected)
    {
        RetryPlan.From(new RetryPolicy { MaxAttempts = maxAttempts }).RetriesOnFailure.ShouldBe(expected);
    }

    [Fact]
    public void Delay_is_the_clamped_backoff_in_seconds()
    {
        RetryPlan.From(new RetryPolicy { MaxAttempts = 3, BackoffSeconds = 2.5 }).Delay.ShouldBe(TimeSpan.FromSeconds(2.5));
    }
}
