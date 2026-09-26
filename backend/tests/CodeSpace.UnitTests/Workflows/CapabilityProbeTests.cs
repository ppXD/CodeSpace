using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins how a worker keeps its answer to a capability it can only settle by trying ("can I seal?"): a proof for good, a failure for one retry interval on a
/// monotonic clock and then probed again, its reason kept — and a re-probe that never makes other launches queue.
/// Caching a transient failure for the process lifetime severed every later network-off brokered run on the worker.
/// </summary>
[Trait("Category", "Unit")]
public class CapabilityProbeTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    [Fact]
    public void A_failure_stands_for_one_interval_then_is_probed_again_and_a_proof_is_kept()
    {
        var now = TimeSpan.Zero;
        var answers = new Queue<string?>(new[] { "ip netns add → exit 1: busy", null });
        var probes = 0;
        var cache = new CapabilityProbe(() => { probes++; return answers.Dequeue(); }, () => now, Interval);

        cache.Holds.ShouldBeFalse();
        cache.UnavailableReason.ShouldBe("ip netns add → exit 1: busy", "the failed step is kept for whoever reports the refusal");

        now += Interval - TimeSpan.FromSeconds(1);
        cache.Holds.ShouldBeFalse();
        probes.ShouldBe(1, "a failure stands until its interval has passed");

        now += TimeSpan.FromSeconds(1);
        cache.Holds.ShouldBeTrue("a transient failure must not outlive its interval");
        cache.UnavailableReason.ShouldBeNull();

        now += Interval * 10;
        cache.Holds.ShouldBeTrue();
        probes.ShouldBe(2, "a proof holds for the process — it is never probed again");
    }

    [Fact]
    public void A_failed_re_probe_starts_a_new_interval_rather_than_re_probing_on_every_call()
    {
        var now = TimeSpan.Zero;
        var probes = 0;
        var cache = new CapabilityProbe(() => { probes++; return "still failing"; }, () => now, Interval);

        cache.Holds.ShouldBeFalse();
        now += Interval;
        cache.Holds.ShouldBeFalse();
        cache.Holds.ShouldBeFalse();
        now += Interval - TimeSpan.FromSeconds(1);
        cache.Holds.ShouldBeFalse();

        probes.ShouldBe(2, "a host that keeps failing is probed once per interval, not once per launch");
    }

    [Fact]
    public async Task Concurrent_first_callers_wait_for_the_first_probe_instead_of_being_refused()
    {
        // A fresh worker that may well seal must not refuse the launches that arrive while it finds out.
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cache = new CapabilityProbe(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); return null; }, () => TimeSpan.Zero, Interval);

        var first = Task.Run(() => cache.Holds);
        started.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue("fixture: the first probe started");
        var second = Task.Run(() => cache.Holds);

        (await Task.WhenAny(second, Task.Delay(300))).ShouldNotBe(second, "a caller arriving during the FIRST probe waits for its answer");

        release.Set();
        (await first).ShouldBeTrue();
        (await second).ShouldBeTrue("and gets the probe's answer, not a refusal");
    }

    [Fact]
    public async Task A_re_probe_in_flight_answers_other_callers_with_the_standing_failure_instead_of_blocking_them()
    {
        var now = TimeSpan.Zero;
        using var reProbing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var cache = new CapabilityProbe(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) return "first probe failed";
            reProbing.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return null;
        }, () => now, Interval);

        cache.Holds.ShouldBeFalse();
        now += Interval;

        var reProbe = Task.Run(() => cache.Holds);
        reProbing.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue("fixture: the re-probe started");

        cache.Holds.ShouldBeFalse("a caller arriving mid-re-probe takes the standing answer at once rather than queueing behind it");

        release.Set();
        (await reProbe).ShouldBeTrue();
        cache.Holds.ShouldBeTrue();
    }
}
