using CodeSpace.Core.Services.Agents;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The heartbeat loop's behaviour, exercised with a counting delegate (no DB, no mocks): it pings on a
/// cadence, a failed ping is reported but doesn't kill the loop, and it returns cleanly on cancellation
/// (never surfacing OperationCanceledException).
///
/// <para>The cadence is driven by a <see cref="FakeTimeProvider"/>, not by the wall clock. The first of these tests
/// asserted on how many real 20ms ticks fitted inside a real 200ms window, and reddened at random on a loaded
/// runner — observed on PR #1297, a diff that touched neither this loop nor anything near it. A test that reds at
/// random is worse than a missing one: it teaches every reader to re-run red instead of reading it, which is how a
/// real regression gets waved through. Advancing a fake clock makes the SAME property exact instead of probable.</para>
/// </summary>
[Trait("Category", "Unit")]
public class HeartbeatLoopTests
{
    [Fact]
    public async Task Pings_once_per_interval_until_cancelled()
    {
        var time = new FakeTimeProvider();
        var interval = TimeSpan.FromSeconds(30);
        var pinged = new SemaphoreSlim(0);
        var count = 0;
        using var cts = new CancellationTokenSource();

        var loop = HeartbeatLoop.RunAsync(
            _ => { Interlocked.Increment(ref count); pinged.Release(); return Task.CompletedTask; },
            interval,
            _ => { },
            cts.Token,
            time);

        Volatile.Read(ref count).ShouldBe(0, "the first ping is deferred by one interval — the claim already stamped an initial heartbeat");

        for (var i = 1; i <= 3; i++)
        {
            await AdvanceUntilPingedAsync(time, pinged, interval, i);

            Volatile.Read(ref count).ShouldBe(i, $"exactly one ping per elapsed interval — after {i} interval(s) there must be {i}, not 'at least' {i}");
        }

        cts.Cancel();
        await loop;   // returns cleanly on cancel — must not throw

        time.Advance(interval);
        (await pinged.WaitAsync(TimeSpan.FromMilliseconds(200))).ShouldBeFalse("a cancelled loop pings no more, however much time passes");
    }

    /// <summary>
    /// Advances the fake clock until the loop pings, rather than advancing once and assuming it was
    /// listening.
    ///
    /// <para>The loop arms its next timer inside Task.Delay AFTER the previous ping returns, so a
    /// single Advance can land in the window before that registration and be missed entirely — the
    /// clock then never moves again and the wait burns its full timeout. That is the race this test
    /// kept losing. Nudging in fractions of an interval cannot fire a timer early, and the count
    /// assertion at the call site is what still proves one ping per interval.</para>
    /// </summary>
    private static async Task AdvanceUntilPingedAsync(FakeTimeProvider time, SemaphoreSlim pinged, TimeSpan interval, int ordinal)
    {
        for (var nudge = 0; nudge < 200; nudge++)
        {
            if (await pinged.WaitAsync(TimeSpan.FromMilliseconds(10))) return;

            time.Advance(interval / 10);
        }

        throw new TimeoutException($"ping {ordinal} never arrived after advancing the fake clock well past its interval");
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
