using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The heartbeat loop's behaviour, exercised with a counting delegate (no DB, no mocks): it pings on a
/// cadence, a failed ping is reported but doesn't kill the loop, and it returns cleanly on cancellation
/// (never surfacing OperationCanceledException). Generous timing margins keep it stable on loaded CI.
/// </summary>
[Trait("Category", "Unit")]
public class HeartbeatLoopTests
{
    [Fact]
    public async Task Pings_repeatedly_until_cancelled()
    {
        var count = 0;
        using var cts = new CancellationTokenSource();

        var loop = HeartbeatLoop.RunAsync(
            _ => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(20),
            _ => { },
            cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        await loop;   // returns cleanly on cancel — must not throw

        count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task A_failing_ping_is_reported_but_does_not_kill_the_loop()
    {
        var pings = 0;
        var errors = 0;
        using var cts = new CancellationTokenSource();

        var loop = HeartbeatLoop.RunAsync(
            _ => { Interlocked.Increment(ref pings); throw new InvalidOperationException("transient db blip"); },
            TimeSpan.FromMilliseconds(20),
            _ => Interlocked.Increment(ref errors),
            cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        await loop;

        pings.ShouldBeGreaterThanOrEqualTo(2);
        errors.ShouldBe(pings);   // every failed ping was reported; none aborted the loop
    }

    [Fact]
    public async Task Returns_without_pinging_when_already_cancelled()
    {
        var count = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await HeartbeatLoop.RunAsync(
            _ => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromSeconds(30),
            _ => { },
            cts.Token);

        count.ShouldBe(0);   // first ping is deferred one interval; cancelled before it
    }
}
