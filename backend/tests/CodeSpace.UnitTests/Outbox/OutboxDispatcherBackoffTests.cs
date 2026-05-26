using CodeSpace.Core.Services.Outbox;
using Shouldly;

namespace CodeSpace.UnitTests.Outbox;

public class OutboxDispatcherBackoffTests
{
    [Theory]
    [InlineData(1, 60)]      // 60 * 2^0 = 60s
    [InlineData(2, 120)]     // 60 * 2^1 = 120s
    [InlineData(3, 240)]     // 60 * 2^2 = 240s
    [InlineData(4, 480)]
    [InlineData(5, 960)]
    [InlineData(6, 1920)]
    [InlineData(7, 3600)]    // 60 * 2^6 = 3840 → capped at 3600
    [InlineData(8, 3600)]    // capped
    [InlineData(10, 3600)]   // capped — MaxAttempts boundary
    public void ComputeBackoff_doubles_until_capped_at_one_hour(int attempts, double expectedSeconds)
    {
        var backoff = OutboxDispatcher.ComputeBackoff(attempts);

        backoff.TotalSeconds.ShouldBe(expectedSeconds);
    }

    [Fact]
    public void ComputeBackoff_never_returns_negative()
    {
        for (var i = 1; i <= 20; i++) OutboxDispatcher.ComputeBackoff(i).ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void MaxAttempts_is_pinned()
    {
        // Operators monitoring dead-letter rates rely on this number. Pin it.
        OutboxDispatcher.MaxAttempts.ShouldBe(10);
    }
}
