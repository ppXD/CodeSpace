using CodeSpace.Core.Services.Outbox;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Outbox;

/// <summary>
/// Rule 8 pinning tests for outbox lease wire-format constants. An accidental rename of
/// <c>OutboxStatus.Claimed</c>'s string form would silently break in-flight claims: the
/// dispatcher would write the new enum name, but existing rows still carry the old name,
/// and the reaper's <c>WHERE status = 'Claimed'</c> SQL would stop matching them. Catch the
/// drift at compile/test time.
/// </summary>
public class OutboxLeaseConstantsTests
{
    [Theory]
    [InlineData(OutboxStatus.Pending,      "Pending")]
    [InlineData(OutboxStatus.Claimed,      "Claimed")]
    [InlineData(OutboxStatus.Completed,    "Completed")]
    [InlineData(OutboxStatus.DeadLettered, "DeadLettered")]
    public void OutboxStatus_string_form_is_pinned(OutboxStatus value, string expected)
    {
        value.ToString().ShouldBe(expected,
            "OutboxStatus enum value names are persisted as strings in outbox_message.status; " +
            "renaming the enum field changes the persisted form and breaks every existing row plus " +
            "the reaper's WHERE-clause. Migrate explicitly if you really need to rename.");
    }

    [Fact]
    public void DefaultLeaseDuration_is_60_seconds()
    {
        OutboxDispatcher.DefaultLeaseDuration.ShouldBe(TimeSpan.FromSeconds(60),
            "60s lease comfortably covers any single handler invocation (webhook register / engine run); " +
            "shorter risks the reaper racing slow-but-fine workers, longer delays recovery from real crashes. " +
            "Change this value with operator awareness — it affects the reaper's recovery latency.");
    }

    [Fact]
    public void MaxAttempts_is_10()
    {
        OutboxDispatcher.MaxAttempts.ShouldBe(10,
            "10 attempts with exponential backoff lets handlers recover from transient outages spanning " +
            "~2 hours total. Lower → premature dead-letter for slow-resolving incidents; higher → operator " +
            "drowning in retry noise for genuinely dead destinations.");
    }
}
