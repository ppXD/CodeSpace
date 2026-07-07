using System.Text.Json;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// 🟢 Unit: the pure schedule + marker logic behind the supervisor's model-plane outage park (P1.1,
/// <see cref="SupervisorInfraPark"/>). Pins the Rule-8 constants (ladder, window, reason literal), the parkable
/// fault split (ONLY genuinely transient infra classes — everything deterministic stays fail-fast), the durable
/// ladder walk (marker round-trip, foreign-payload isolation), and the jittered delay bands.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorInfraParkTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-07T10:00:00Z");

    [Fact]
    public void The_ladder_the_window_and_the_reason_are_pinned()
    {
        // Shrinking the ladder re-storms a recovering provider; shrinking the window abandons runs early; the
        // reason literal is what the journal + the Stopped status surface. Hard-pin all three (Rule 8).
        SupervisorInfraPark.Schedule.ShouldBe(new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60) });
        SupervisorInfraPark.MaxParkWindow.ShouldBe(TimeSpan.FromHours(24));
        SupervisorStopReasons.ModelPlaneUnavailable.ShouldBe("model plane unavailable");
    }

    [Theory]
    [InlineData(LlmErrorCategory.Transient, true)]              // gateway blip / outage — a wake may outlive it
    [InlineData(LlmErrorCategory.RateLimited, true)]            // 429 storm — the ladder IS the right response
    [InlineData(LlmErrorCategory.AuthFailed, false)]            // operator must fix the credential NOW — parking hides it
    [InlineData(LlmErrorCategory.ContextLengthExceeded, false)] // deterministic — a wake changes nothing
    [InlineData(LlmErrorCategory.BadRequest, false)]
    [InlineData(LlmErrorCategory.ContentFiltered, false)]
    [InlineData(LlmErrorCategory.Malformed, false)]
    public void Only_genuinely_transient_infra_faults_are_parkable(LlmErrorCategory category, bool parkable)
    {
        SupervisorInfraPark.IsParkable(category).ShouldBe(parkable);
    }

    [Fact]
    public void A_first_fault_starts_the_ladder_at_park_one()
    {
        var state = SupervisorInfraPark.Next(resumePayload: null, Now);

        state.Parks.ShouldBe(1);
        state.FirstParkedAtUtc.ShouldBe(Now, "the window anchors at the FIRST park");
        state.WindowExhausted.ShouldBeFalse();
    }

    [Fact]
    public void A_marker_round_trip_continues_the_ladder_durably()
    {
        // Park 1's marker, fed back as the wake's resume payload (exactly what the deadline injects), must
        // continue the ladder — parks advance, the window anchor is PRESERVED across the suspend cycle.
        var first = SupervisorInfraPark.Next(null, Now);
        var marker = SupervisorInfraPark.Marker(first, "gateway 503");

        var second = SupervisorInfraPark.Next(marker, Now + TimeSpan.FromMinutes(1));

        second.Parks.ShouldBe(2);
        second.FirstParkedAtUtc.ShouldBe(Now, "the anchor survives the round trip — the 24h window is measured from the FIRST park, not the latest");
        second.WindowExhausted.ShouldBeFalse();
    }

    [Fact]
    public void The_window_exhausts_measured_from_the_first_park()
    {
        var first = SupervisorInfraPark.Next(null, Now);
        var marker = SupervisorInfraPark.Marker(first, "gateway 503");

        var afterWindow = SupervisorInfraPark.Next(marker, Now + SupervisorInfraPark.MaxParkWindow);

        afterWindow.WindowExhausted.ShouldBeTrue("24h of outage from the first park → stop honestly, never park forever");
    }

    [Theory]
    [InlineData("""{"resumed_at":"2026-07-07T10:00:00Z"}""")]           // a bare timer/self-advance wake marker
    [InlineData("""{"action":"approve","by":"user"}""")]                // a human answer
    [InlineData("""{"infraPark":false,"parks":9}""")]                   // an explicit non-marker
    [InlineData("""{"infraPark":true,"parks":3}""")]                    // marker missing its window anchor → treated fresh (defensive)
    [InlineData("""[1,2,3]""")]                                         // not even an object
    public void A_foreign_resume_payload_reads_as_a_fresh_ladder(string payloadJson)
    {
        var state = SupervisorInfraPark.Next(JsonDocument.Parse(payloadJson).RootElement, Now);

        state.Parks.ShouldBe(1, "only the park's OWN marker continues the ladder — every other resume shape starts fresh");
        state.WindowExhausted.ShouldBeFalse();
    }

    [Theory]
    [InlineData(1, 60)]     // park 1 → 1m rung
    [InlineData(2, 300)]    // park 2 → 5m
    [InlineData(3, 900)]    // park 3 → 15m
    [InlineData(4, 3600)]   // park 4 → 60m
    [InlineData(9, 3600)]   // beyond the ladder → clamped to the last rung
    [InlineData(0, 60)]     // defensive: a zero/negative park reads the first rung
    public void The_delay_walks_the_ladder_within_the_jitter_band(int parks, int rungSeconds)
    {
        var delay = SupervisorInfraPark.DelayFor(parks);

        delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(rungSeconds * 0.8), "jitter floor is −20%");
        delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(rungSeconds * 1.2), "jitter roof is +20%");
    }

    [Fact]
    public void The_marker_names_the_fault_for_the_parked_run_detail()
    {
        var marker = SupervisorInfraPark.Marker(SupervisorInfraPark.Next(null, Now), "gateway 503: upstream connect error");

        marker.GetProperty(SupervisorInfraPark.MarkerField).GetBoolean().ShouldBeTrue();
        marker.GetProperty("parks").GetInt32().ShouldBe(1);
        marker.GetProperty("error").GetString().ShouldBe("gateway 503: upstream connect error", "the parked run detail shows WHY the run is waiting");
    }
}
