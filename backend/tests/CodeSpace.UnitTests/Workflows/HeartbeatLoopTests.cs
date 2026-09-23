using CodeSpace.Core.Services.Agents;
using CodeSpace.UnitTests.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The heartbeat loop's behaviour, exercised with a counting delegate (no DB, no mocks): it pings on a
/// cadence, a failed ping is reported but doesn't kill the loop, and it returns cleanly on cancellation
/// (never surfacing OperationCanceledException).
///
/// <para>Every cadence here is driven by a <see cref="FakeTimeProvider"/>, not by the wall clock. Both counting
/// tests below once asserted on how many real 20ms ticks fitted inside a real 200ms window, and each reddened at
/// random on a loaded runner — the ping test on PR #1297, the failing-ping test on #2001 (<c>pings should be
/// &gt;= 2 but was 1</c>), both diffs that touched neither this loop nor anything near it. A test that reds at
/// random is worse than a missing one: it teaches every reader to re-run red instead of reading it, which is how a
/// real regression gets waved through. Advancing a fake clock makes the SAME property exact instead of probable —
/// and turns "at least 2 pings" into "exactly one per elapsed interval", which is the property that was meant.</para>
/// </summary>
[Trait("Category", "Unit")]
public class HeartbeatLoopTests
{
    /// <summary>How far short of a full interval the "not yet" check stands.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Pings_once_per_interval_until_cancelled()
    {
        var time = new HeartbeatClock();
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
            await AdvanceOneIntervalAsync(time, pinged, interval, i);

            Volatile.Read(ref count).ShouldBe(i, $"exactly one ping per elapsed interval — after {i} interval(s) there must be {i}, not 'at least' {i}");
        }

        cts.Cancel();
        await loop;   // returns cleanly on cancel — must not throw

        time.Advance(interval);
        (await pinged.WaitAsync(TimeSpan.FromMilliseconds(200))).ShouldBeFalse("a cancelled loop pings no more, however much time passes");
    }

    /// <summary>
    /// Advances the fake clock across ONE interval in two moves and pins what each must do: a <see cref="Step"/> short of
    /// the interval the loop must still be asleep, and the step that completes it must produce exactly the beat. A loop
    /// that sleeps anything but the interval it was handed reds on one side or the other — where nudging in tenths until
    /// SOMETHING arrived let any sleep between a tenth of the interval and twenty of them pass.
    ///
    /// <para>The loop arms its next timer inside Task.Delay AFTER the previous ping returns, so an Advance issued before
    /// that registration moves a clock nothing is waiting on yet — the race this test once kept losing. Waiting for the
    /// timer first closes it without letting any time pass.</para>
    /// </summary>
    private static async Task AdvanceOneIntervalAsync(HeartbeatClock time, SemaphoreSlim ticked, TimeSpan interval, int ordinal)
    {
        await time.NextTimerAsync();

        time.Advance(interval - Step);
        (await ticked.WaitAsync(TimeSpan.FromMilliseconds(100))).ShouldBeFalse($"beat {ordinal} arrived {Step.TotalSeconds}s before its interval completed — the loop sleeps less than it was handed");

        time.Advance(Step);
        (await ticked.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue($"beat {ordinal} never arrived once its interval completed — the loop sleeps longer than it was handed, or stopped repeating");
    }

    [Fact]
    public async Task A_failing_ping_is_reported_but_does_not_kill_the_loop()
    {
        var time = new HeartbeatClock();
        var interval = TimeSpan.FromSeconds(30);
        var reported = new SemaphoreSlim(0);
        var pings = 0;
        var errors = 0;
        using var cts = new CancellationTokenSource();

        // The semaphore is released from onPingError, not from the ping: by the time it signals, BOTH counters
        // for that tick have settled, so the assertions below read a consistent pair rather than a half-applied one.
        var loop = HeartbeatLoop.RunAsync(
            _ => { Interlocked.Increment(ref pings); throw new InvalidOperationException("transient db blip"); },
            interval,
            _ => { Interlocked.Increment(ref errors); reported.Release(); },
            cts.Token,
            time);

        for (var i = 1; i <= 3; i++)
        {
            await AdvanceOneIntervalAsync(time, reported, interval, i);

            Volatile.Read(ref pings).ShouldBe(i, "a throwing ping must not stop, skip, or double the cadence");
            Volatile.Read(ref errors).ShouldBe(i, "every failed ping is reported exactly once — none aborted the loop");
        }

        cts.Cancel();
        await loop;   // a loop whose every ping threw still returns cleanly on cancel, never surfacing the failure
    }

    /// <summary>
    /// The reporter is the caller's code, and every caller awaits this loop in the <c>finally</c> around the work it
    /// protects, so a reporter that throws may end the loop no more than a failing ping may. It did: the report ran outside
    /// the ping's guard, so a reporter's fault faulted the loop — and the awaiting <c>finally</c> surfaced it IN PLACE of
    /// the result the loop was keeping alive — while a reporter's <see cref="OperationCanceledException"/> ended the loop
    /// early and without a word, stopping liveness while the work ran on. Both are a loop that completed by something other
    /// than its own cancellation, and both red here as a loop that never arms its next beat.
    /// </summary>
    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task A_reporter_that_throws_neither_faults_nor_ends_the_loop(Type faultType)
    {
        var time = new HeartbeatClock();
        var interval = TimeSpan.FromSeconds(30);
        var reported = new SemaphoreSlim(0);
        var pings = 0;
        using var cts = new CancellationTokenSource();

        // Released BEFORE the reporter throws, since nothing after the throw runs — and after the ping has counted, so
        // each signal reads a settled count.
        var loop = HeartbeatLoop.RunAsync(
            _ => { Interlocked.Increment(ref pings); throw new InvalidOperationException("transient db blip"); },
            interval,
            _ => { reported.Release(); throw (Exception)Activator.CreateInstance(faultType, "the reporter itself failed")!; },
            cts.Token,
            time);

        for (var i = 1; i <= 3; i++)
        {
            await AdvanceOneIntervalAsync(time, reported, interval, i);

            Volatile.Read(ref pings).ShouldBe(i, "a reporter that threw must not stop, skip, or double the cadence");
        }

        cts.Cancel();

        // Recorded rather than asserted with Should.NotThrowAsync, which passes a CANCELED task without a word. Bounded, so
        // a loop that ignores the cancel fails instead of hanging.
        var escaped = await Record.ExceptionAsync(() => loop.WaitAsync(TimeSpan.FromSeconds(10)));

        escaped.ShouldBeNull("the loop completes only by its own cancellation, and quietly — whatever escapes it is what the awaiting finally surfaces in place of the result it protects");
    }

    [Fact]
    public async Task Returns_without_pinging_when_already_cancelled()
    {
        var time = new FakeTimeProvider();
        var count = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await HeartbeatLoop.RunAsync(
            _ => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromSeconds(30),
            _ => { },
            cts.Token,
            time);

        count.ShouldBe(0);   // first ping is deferred one interval; cancelled before it
    }

    /// <summary>
    /// The clock is a REQUIRED parameter, so no call site can fall back to the wall clock by saying nothing.
    ///
    /// <para>It was optional-defaulting-to-System first, and both executor call sites took that default — the seam
    /// existed and production ignored it, which is exactly how a "we made it testable" claim goes stale. A default
    /// here is re-addable in one character and would red nothing else, so this pins the absence of one.</para>
    /// </summary>
    [Fact]
    public void The_clock_cannot_be_omitted_by_a_call_site()
    {
        var clock = typeof(HeartbeatLoop).GetMethod(nameof(HeartbeatLoop.RunAsync))!.GetParameters().Single(p => p.ParameterType == typeof(TimeProvider));

        clock.HasDefaultValue.ShouldBeFalse("an optional clock is how both production heartbeats silently stayed on the wall clock");
    }
}
