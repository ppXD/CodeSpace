using System.Text.Json;
using CodeSpace.Core.Services.Supervisor;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// 🟢 Unit: P4.3's chaos-lane #1 (sustained 429 storm) at its cheapest, fastest seam — <see cref="SupervisorInfraPark"/>
/// is pure statics with the clock passed in explicitly, so a genuinely-repeated fault can be walked through EVERY
/// ladder rung (not just the first one or two, which <c>SupervisorInfraParkTests</c> and the real-Postgres
/// <c>SupervisorInfraParkFlowTests</c> already cover) with zero real sleeps. Closes the exact gap a live 30-min+ 429
/// storm would exercise: does the ladder keep advancing correctly for as long as the storm persists, and does it
/// still stop honestly (never fake a Success) once the whole 24h window elapses? No fixture, no DB, no live model —
/// this is the park LADDER half of the storm; <c>RetryingSupervisorDeciderDecoratorTests.Many_consecutive_exhaustions_across_simulated_wakes_never_fabricate_a_decision</c>
/// covers the IN-CALL RETRY half (the decorator itself never fabricating a decision across many exhaust cycles).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorInfraParkStormTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-09T00:00:00Z");

    [Fact]
    public void A_sustained_storm_walks_every_ladder_rung_then_the_window_exhausts_never_faking_success()
    {
        JsonElement? marker = null;
        var now = Start;
        var lastParks = 0;

        // Walk the ladder far past its 4 rungs (40 consecutive parks) — each iteration is the node re-catching
        // ANOTHER injected transient/rate-limit fault on wake, exactly like AgentSupervisorNode.ParkForInfraOrStopAsync
        // would on a real 30-minute-plus 429 storm. Zero real sleeps: `now` is a synthetic clock we advance ourselves.
        for (var i = 1; i <= 40; i++)
        {
            var state = SupervisorInfraPark.Next(marker, now);

            if (state.WindowExhausted) break;   // the storm crossed 24h — the node would force-stop HERE, never fake success

            state.Parks.ShouldBe(i, "the ladder must advance by exactly one per genuinely-repeated fault — never skip or double-count a park");
            lastParks = state.Parks;

            var delay = SupervisorInfraPark.DelayFor(state.Parks);
            delay.ShouldBeLessThanOrEqualTo(SupervisorInfraPark.Schedule[^1] * 1.2, "no rung ever exceeds the last (60m) rung, even deep into the storm");

            marker = SupervisorInfraPark.Marker(state, $"gateway 429 (park {i})");
            now += delay;   // simulate the deadline wake firing exactly at the jittered delay
        }

        // The rungs cap at 60m, so ~24-30 parks cross the 24h window comfortably inside the 40-park ceiling —
        // assert we actually REACHED exhaustion, not merely that the loop ran out.
        lastParks.ShouldBeLessThan(40, "the storm must cross the whole 24h window well before 40 synthetic parks — otherwise this test isn't proving what it claims");
        (now - Start).ShouldBeGreaterThanOrEqualTo(SupervisorInfraPark.MaxParkWindow, "the synthetic storm must span the whole 24h window, not stop short of it");

        var final = SupervisorInfraPark.Next(marker, now);
        final.WindowExhausted.ShouldBeTrue("a storm that never clears must eventually force an honest stop, never park forever");

        // The only honest ending past this line is SupervisorStopReasons.ModelPlaneUnavailable (a degraded Stopped) —
        // SupervisorInfraPark itself never produces anything decision-shaped, so a fabricated Success is structurally
        // impossible from this seam; pinning the reason string here documents what the node's own re-entry reports.
        SupervisorStopReasons.ModelPlaneUnavailable.ShouldBe("model plane unavailable");
    }

    [Fact]
    public void The_storm_clearing_mid_ladder_recovers_cleanly_without_ever_reaching_the_window()
    {
        // A storm that persists through parks 1-3 then clears (the 4th wake decides successfully) must NOT force-stop
        // — the node simply stops calling SupervisorInfraPark and returns its real decision. Proven at the SAME
        // pure-logic seam: nothing here ever produces anything but park bookkeeping, so "recovery" is just the
        // absence of a further call — there is no decision-shaped value this class could fabricate.
        var now = Start;
        JsonElement? marker = null;

        for (var i = 1; i <= 3; i++)
        {
            var state = SupervisorInfraPark.Next(marker, now);

            state.WindowExhausted.ShouldBeFalse($"park {i} of a 3-park storm must stay well inside the 24h window");
            state.Parks.ShouldBe(i);

            marker = SupervisorInfraPark.Marker(state, "gateway 429");
            now += SupervisorInfraPark.DelayFor(state.Parks);
        }

        // Wake 4: the brain call SUCCEEDS this time — production code (AgentSupervisorNode.RunAsync) takes the
        // "return the real decision" branch and never calls SupervisorInfraPark.Next again for this outage. Nothing
        // left to assert on this class: Next/Marker/DelayFor only ever return park bookkeeping, never a decision, so
        // a recovery is structurally incapable of reading as a fabricated Success.
    }
}
