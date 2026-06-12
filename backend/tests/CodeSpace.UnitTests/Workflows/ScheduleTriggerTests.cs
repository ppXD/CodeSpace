using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Workflows.RunSources.Schedule;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Unit coverage for the schedule trigger's pure pieces: the cron occurrence calculator, the
/// recurring-job descriptor pins, and the look-back env-var constant. The producer's DB-touching
/// fire logic is covered at the integration tier (<c>ScheduleTriggerFlowTests</c>).
/// </summary>
[Trait("Category", "Unit")]
public class ScheduleTriggerTests
{
    // A fixed reference instant so cron math is deterministic. 10:05:30 UTC.
    private static readonly DateTimeOffset Now = new(2026, 6, 12, 10, 5, 30, TimeSpan.Zero);

    [Fact]
    public void Minutely_cron_yields_each_minute_boundary_in_the_window()
    {
        // Window (10:03:30, 10:05:30] contains the :04:00 and :05:00 boundaries.
        var ok = ScheduleOccurrenceCalculator.TryGetOccurrences("* * * * *", Now.AddMinutes(-2), Now, out var occ);

        ok.ShouldBeTrue();
        occ.Select(o => o.ToUniversalTime().ToString("HH:mm:ss")).ShouldBe(new[] { "10:04:00", "10:05:00" });
    }

    [Fact]
    public void From_is_exclusive_and_to_is_inclusive()
    {
        // Window (10:04:00, 10:05:00]: the :04:00 boundary is excluded (from-exclusive),
        // the :05:00 boundary included (to-inclusive). Pins the half-open semantics that keep
        // a minutely cron from firing the same minute twice across adjacent ticks.
        var ok = ScheduleOccurrenceCalculator.TryGetOccurrences(
            "* * * * *",
            new DateTimeOffset(2026, 6, 12, 10, 4, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 12, 10, 5, 0, TimeSpan.Zero),
            out var occ);

        ok.ShouldBeTrue();
        occ.Select(o => o.ToUniversalTime().ToString("HH:mm:ss")).ShouldBe(new[] { "10:05:00" });
    }

    [Fact]
    public void Every_five_minutes_only_matches_aligned_boundary()
    {
        var ok = ScheduleOccurrenceCalculator.TryGetOccurrences("*/5 * * * *", Now.AddMinutes(-2), Now, out var occ);

        ok.ShouldBeTrue();
        occ.Select(o => o.ToUniversalTime().ToString("HH:mm:ss")).ShouldBe(new[] { "10:05:00" });
    }

    [Fact]
    public void Daily_cron_not_due_in_window_yields_no_occurrences()
    {
        var ok = ScheduleOccurrenceCalculator.TryGetOccurrences("0 0 * * *", Now.AddMinutes(-2), Now, out var occ);

        ok.ShouldBeTrue("a valid cron with no occurrences in the window is success-with-empty, not failure");
        occ.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a cron")]
    [InlineData("99 * * * *")]          // minute out of range
    [InlineData("0 * * * * *")]          // 6-field (seconds) — rejected; we only accept standard 5-field
    public void Invalid_cron_returns_false_with_empty_occurrences(string cron)
    {
        var ok = ScheduleOccurrenceCalculator.TryGetOccurrences(cron, Now.AddMinutes(-2), Now, out var occ);

        ok.ShouldBeFalse();
        occ.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("* * * * *", true)]
    [InlineData("0 9 * * 1-5", true)]
    [InlineData("*/15 * * * *", true)]
    [InlineData("nope", false)]
    [InlineData("0 * * * * *", false)]
    public void IsValid_matches_standard_cron(string cron, bool expected)
    {
        ScheduleOccurrenceCalculator.IsValid(cron).ShouldBe(expected);
    }

    [Fact]
    public void Recurring_job_descriptor_is_pinned()
    {
        var job = new ScheduleTriggerRecurringJob(null!);

        job.JobId.ShouldBe(nameof(ScheduleTriggerRecurringJob));
        job.CronExpression.ShouldBe("* * * * *", "the producer must tick every minute — the finest schedule granularity the platform offers");
    }

    [Fact]
    public void Lookback_env_var_constant_name_pinned()
    {
        // Renaming this constant silently breaks any operator who pinned a look-back override
        // via env. Hard-pin so a rename is a compile-visible decision.
        ScheduleTriggerService.LookbackSecondsEnvVar.ShouldBe("CODESPACE_SCHEDULE_TRIGGER_LOOKBACK_SECONDS");
    }
}
