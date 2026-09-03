using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the two pure folds behind the durable north-star trend — daily bucketing
/// (<see cref="RunScorecardTrendService.Bucket"/>) and the lesson A/B slice
/// (<see cref="LessonArmSlicer.Slice"/>) — plus the scorer-version pin every persisted row carries.
///
/// <para>What these tests exist to stop: (1) a day with no runs rendering as a 0% point, which turns silence into
/// a regression; (2) an arm-less run being folded into the <c>none</c> control, which would let unmeasured runs
/// dilute the control group and make an A/B unreadable; (3) the scorer version drifting silently, which would let
/// a trend line span two different definitions of "solved with delivery".</para>
/// </summary>
[Trait("Category", "Unit")]
public class RunScorecardTrendTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static RunScorecardTrendService.TrendRow Row(DateTimeOffset at, bool solved = true, bool delivered = true, bool headline = true, decimal? cost = null, decimal? brain = null, string? arm = null) =>
        new(at, solved, delivered, headline, cost, brain, arm);

    private static ArmedRunScore Armed(string? arm, bool solved = true, bool delivered = true, bool headline = true) =>
        new() { LessonArm = arm, Solved = solved, Delivered = delivered, UnattendedSolvedWithDelivery = headline };

    // ─── Bucketing ────────────────────────────────────────────────────────────────

    [Fact]
    public void Runs_on_the_same_utc_day_fold_into_one_bucket_with_the_days_rate()
    {
        var buckets = RunScorecardTrendService.Bucket([
            Row(Day1.AddHours(1), headline: true),
            Row(Day1.AddHours(9), headline: false),
            Row(Day1.AddHours(23), headline: true),
        ]);

        buckets.ShouldHaveSingleItem();
        buckets[0].Day.ShouldBe(Day1);
        buckets[0].Runs.ShouldBe(3);
        buckets[0].UnattendedSolvedWithDeliveryRuns.ShouldBe(2);
        buckets[0].UnattendedSolveWithDeliveryRate.ShouldBe(2d / 3);
    }

    [Fact]
    public void A_day_with_no_runs_is_absent_rather_than_a_zero_rate_point()
    {
        var buckets = RunScorecardTrendService.Bucket([Row(Day1), Row(Day1.AddDays(2))]);

        buckets.Count.ShouldBe(2, "the empty middle day is ABSENT — 'nothing ran' and 'nothing solved' are different claims and only one belongs on a rate line");
        buckets.Select(b => b.Day).ShouldBe([Day1, Day1.AddDays(2)], ignoreOrder: false);
    }

    [Fact]
    public void Buckets_come_back_oldest_first()
    {
        var buckets = RunScorecardTrendService.Bucket([Row(Day1.AddDays(3)), Row(Day1), Row(Day1.AddDays(1))]);

        buckets.Select(b => b.Day).ShouldBe([Day1, Day1.AddDays(1), Day1.AddDays(3)], ignoreOrder: false);
    }

    [Fact]
    public void A_bucket_sums_only_priced_runs_and_reports_null_when_none_were_priceable()
    {
        var priced = RunScorecardTrendService.Bucket([Row(Day1, cost: 1.50m, brain: 0.25m), Row(Day1, cost: null, brain: null)]);
        var unpriced = RunScorecardTrendService.Bucket([Row(Day1, cost: null, brain: null)]);

        priced[0].CostUsd.ShouldBe(1.50m, "the unpriceable sibling is skipped, never counted as $0");
        priced[0].BrainPlaneUsd.ShouldBe(0.25m);
        unpriced[0].CostUsd.ShouldBeNull("no priceable run is NOT a real $0");
        unpriced[0].BrainPlaneUsd.ShouldBeNull();
    }

    [Fact]
    public void An_empty_window_produces_no_buckets()
    {
        RunScorecardTrendService.Bucket([]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, 1)]                                   // clamped up — a zero-day window would measure nothing
    [InlineData(1, 1)]
    [InlineData(28, 28)]
    [InlineData(100000, GetScorecardTrendQuery.MaxDays)] // clamped down — one bucket per day, so the payload is bounded
    public void The_window_is_clamped_and_starts_at_utc_midnight(int requested, int effectiveDays)
    {
        var since = RunScorecardTrendService.SinceFor(requested);

        since.Offset.ShouldBe(TimeSpan.Zero);
        since.TimeOfDay.ShouldBe(TimeSpan.Zero, "the horizon starts at a UTC day boundary so buckets line up with it");
        (new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero) - since).Days.ShouldBe(effectiveDays - 1);
    }

    // ─── By-arm slicing ───────────────────────────────────────────────────────────

    [Fact]
    public void Each_arm_gets_its_own_rate()
    {
        var slices = LessonArmSlicer.Slice([
            Armed(LessonArms.Injected, headline: true),
            Armed(LessonArms.Injected, headline: true),
            Armed(LessonArms.Injected, headline: false),
            Armed(LessonArms.Withheld, headline: true),
            Armed(LessonArms.Withheld, headline: false),
        ]);

        var injected = slices.Single(s => s.Arm == LessonArms.Injected);
        var withheld = slices.Single(s => s.Arm == LessonArms.Withheld);

        injected.Runs.ShouldBe(3);
        injected.UnattendedSolvedWithDeliveryRuns.ShouldBe(2);
        injected.UnattendedSolveWithDeliveryRate.ShouldBe(2d / 3);
        withheld.Runs.ShouldBe(2);
        withheld.UnattendedSolveWithDeliveryRate.ShouldBe(0.5d);
    }

    [Fact]
    public void An_armless_run_is_unmeasured_and_never_folded_into_the_none_control()
    {
        var slices = LessonArmSlicer.Slice([Armed(null), Armed(LessonArms.None), Armed("  ")]);

        slices.Single(s => s.Arm == LessonArmSlicer.Unmeasured).Runs.ShouldBe(2, "a run with no decision ledger (and a blank arm) was never IN the experiment");
        slices.Single(s => s.Arm == LessonArms.None).Runs.ShouldBe(1, "'none' is the empty-lesson CONTROL — merging unmeasured runs into it would dilute the group the injected arm is compared against");
    }

    [Fact]
    public void An_arm_with_no_runs_has_no_slice()
    {
        var slices = LessonArmSlicer.Slice([Armed(LessonArms.Injected)]);

        slices.ShouldHaveSingleItem().Arm.ShouldBe(LessonArms.Injected);
        slices.ShouldNotContain(s => s.Arm == LessonArms.Withheld, "a 0/0 rate is not a measurement");
    }

    [Fact]
    public void Slices_render_in_a_fixed_order_so_two_windows_read_side_by_side()
    {
        var slices = LessonArmSlicer.Slice([Armed(null), Armed(LessonArms.None), Armed(LessonArms.Withheld), Armed(LessonArms.Injected)]);

        slices.Select(s => s.Arm).ShouldBe([LessonArms.Injected, LessonArms.Withheld, LessonArms.None, LessonArmSlicer.Unmeasured], ignoreOrder: false);
    }

    [Fact]
    public void An_empty_population_produces_no_slices()
    {
        LessonArmSlicer.Slice([]).ShouldBeEmpty();
    }

    [Fact]
    public void The_slice_counts_solved_and_delivered_independently_of_the_headline()
    {
        var slices = LessonArmSlicer.Slice([
            Armed(LessonArms.Injected, solved: true, delivered: false, headline: false),
            Armed(LessonArms.Injected, solved: false, delivered: true, headline: false),
        ]);

        var injected = slices.ShouldHaveSingleItem();
        injected.SolvedRuns.ShouldBe(1);
        injected.DeliveredRuns.ShouldBe(1);
        injected.UnattendedSolvedWithDeliveryRuns.ShouldBe(0, "neither run cleared both gates");
        injected.UnattendedSolveWithDeliveryRate.ShouldBe(0d);
    }

    // ─── The pinned contracts ─────────────────────────────────────────────────────

    [Fact]
    public void The_scorer_version_literal_is_pinned()
    {
        // Every persisted run_scorecard row stamps this string. Changing it silently would let one trend line span
        // two different definitions of "solved with delivery"; changing it WITHOUT bumping is worse — the old rows
        // then claim a contract they were not measured under. Either way it must be a deliberate, visible edit.
        UnattendedDeliveryScorer.ScorerVersion.ShouldBe("unattended-delivery/v1");
    }

    [Fact]
    public void The_supervisor_decision_call_kind_literal_is_pinned()
    {
        // The brain-model column is resolved by matching this OPEN kind label against the run's interaction ledger.
        // The producing side (SupervisorTurnService's LlmCallScope) writes the literal directly, so there is no
        // shared enum to protect the agreement — this pin is it. Drift silently blanks brain_model on every row.
        RunScorecardWriter.SupervisorDecisionCallKind.ShouldBe("supervisor.decision");
    }
}
