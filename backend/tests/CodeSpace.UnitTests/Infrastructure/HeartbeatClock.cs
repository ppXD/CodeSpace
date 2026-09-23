using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Infrastructure;

/// <summary>
/// A fake clock for a heartbeat loop that says when the loop has ARMED its next sleep. A loop registers its next timer
/// only after the previous beat's work returns, so an advance issued before that registration moves a clock nothing is
/// waiting on yet: the beat is then missed, or lands late. Waiting for the timer before every advance lets the fake
/// clock decide not only HOW MANY beats land but exactly WHEN each one does — which is what lets a test pin the interval
/// itself instead of only the count. The same rendezvous <c>AgentProgressLeaseTests</c> uses for its renewal loop.
/// </summary>
public sealed class HeartbeatClock : TimeProvider
{
    private readonly FakeTimeProvider _time = new();
    private readonly SemaphoreSlim _armed = new(0);

    public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();
    public override long GetTimestamp() => _time.GetTimestamp();
    public override long TimestampFrequency => _time.TimestampFrequency;

    public void Advance(TimeSpan amount) => _time.Advance(amount);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _time.CreateTimer(callback, state, dueTime, period);
        _armed.Release();
        return timer;
    }

    /// <summary>Returns once the loop is asleep on a timer armed at the clock's current instant. The bound only detects a loop that never re-arms; it never lets time pass on the fake clock.</summary>
    public async Task NextTimerAsync() => (await _armed.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue("the heartbeat loop must arm its next sleep — it stopped repeating, or it is not sleeping on the clock it was handed");
}
