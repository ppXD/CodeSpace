using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Folds a team's durable <c>run_scorecard</c> rows into daily buckets + lesson-arm slices, with the two
/// populations the rate cannot see (parks, pre-protocol runs) counted beside it. Thin (Rule 16) — the service owns
/// the team-scoped windowed queries; the bucketing and the by-arm arithmetic are pure statics
/// (<see cref="Bucket"/> / <see cref="LessonArmSlicer"/>) so both unit-test without a database.
/// </summary>
public sealed class RunScorecardTrendService : IRunScorecardTrendService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public RunScorecardTrendService(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<RunScorecardTrend> ComputeAsync(Guid teamId, int days, CancellationToken cancellationToken)
    {
        var since = SinceFor(days);

        var rows = await _db.RunScorecard.AsNoTracking()
            .Where(s => s.TeamId == teamId && s.CompletedAt >= since)
            .OrderBy(s => s.CompletedAt)
            .Select(s => new TrendRow(s.CompletedAt, s.Solved, s.Delivered, s.UnattendedSolvedWithDelivery, s.CostUsd, s.BrainPlaneUsd, s.LessonArm))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var unscored = await UnscoredRunsAsync(teamId, since, cancellationToken).ConfigureAwait(false);

        return new RunScorecardTrend
        {
            Since = since,
            ScoredRuns = rows.Count,
            Buckets = Bucket(rows, unscored),
            ByLessonArm = LessonArmSlicer.Slice(rows.Select(r => r.ToArmedScore()).ToList()),
        };
    }

    /// <summary>The window's inclusive start: midnight UTC, <paramref name="days"/>-1 days before today, with the requested horizon clamped so the payload (one bucket per day) is bounded rather than trusted.</summary>
    public static DateTimeOffset SinceFor(int days)
    {
        var clamped = Math.Clamp(days, 1, GetScorecardTrendQuery.MaxDays);

        return new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(-(clamped - 1));
    }

    /// <summary>
    /// The two populations a north-star rate structurally cannot include, both of which make the rate look better by
    /// leaving: runs still SUSPENDED (parked to ask a human — they never terminalize, so they never enter a
    /// denominator) and PRE-PROTOCOL terminal runs (visible, never scored). Counted per day so a park-heavy day is
    /// legible beside its own rate instead of silently thinning it.
    ///
    /// <para>Parks are windowed on CreatedDate because a parked run has no completion; legacy runs on CompletedAt,
    /// matching the scored rows they sit beside.</para>
    /// </summary>
    private async Task<IReadOnlyList<UnscoredRun>> UnscoredRunsAsync(Guid teamId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var suspended = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.Status == WorkflowRunStatus.Suspended && r.CreatedDate >= since)
            .Select(r => r.CreatedDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var legacy = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.TeamId == teamId
                        && r.CompletionPolicyVersion == null
                        && (r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled)
                        && r.CompletedAt != null && r.CompletedAt >= since)
            .Select(r => r.CompletedAt!.Value)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return suspended.Select(at => new UnscoredRun(at, Suspended: true))
            .Concat(legacy.Select(at => new UnscoredRun(at, Suspended: false)))
            .ToList();
    }

    /// <summary>
    /// One bucket per UTC day that saw ANY activity, oldest first — scored runs, parks, and pre-protocol runs all
    /// open a day. A day nothing happened on is ABSENT rather than a zero-rate point: "we solved nothing that day"
    /// and "nothing ran that day" are different claims, and only one belongs on a rate line. A day that saw only
    /// parks appears with <c>Runs = 0</c> and a NULL rate — visible, and honestly unmeasured.
    ///
    /// <para>Pure so the bucketing unit-tests without a database.</para>
    /// </summary>
    public static IReadOnlyList<RunScorecardTrendBucket> Bucket(IReadOnlyList<TrendRow> rows, IReadOnlyList<UnscoredRun> unscored)
    {
        var scoredByDay = rows.GroupBy(r => DayOf(r.CompletedAt)).ToDictionary(g => g.Key, g => g.ToList());
        var unscoredByDay = unscored.GroupBy(u => DayOf(u.At)).ToDictionary(g => g.Key, g => g.ToList());

        return scoredByDay.Keys
            .Concat(unscoredByDay.Keys)
            .Distinct()
            .OrderBy(day => day)
            .Select(day => Fold(day, scoredByDay.GetValueOrDefault(day, []), unscoredByDay.GetValueOrDefault(day, [])))
            .ToList();
    }

    private static DateTimeOffset DayOf(DateTimeOffset at) => new(at.UtcDateTime.Date, TimeSpan.Zero);

    private static RunScorecardTrendBucket Fold(DateTimeOffset day, IReadOnlyList<TrendRow> scored, IReadOnlyList<UnscoredRun> unscored) => new()
    {
        Day = day,
        Runs = scored.Count,
        SolvedRuns = scored.Count(r => r.Solved),
        DeliveredRuns = scored.Count(r => r.Delivered),
        UnattendedSolvedWithDeliveryRuns = scored.Count(r => r.UnattendedSolvedWithDelivery),
        // NULL, not 0, when nothing scored: a day that only parked has no rate to report, and rendering 0% would
        // read as "everything failed" when the truth is "nothing finished."
        UnattendedSolveWithDeliveryRate = scored.Count == 0 ? null : (double)scored.Count(r => r.UnattendedSolvedWithDelivery) / scored.Count,
        SuspendedRuns = unscored.Count(u => u.Suspended),
        LegacyRuns = unscored.Count(u => !u.Suspended),
        CostUsd = SumOrNull(scored.Select(r => r.CostUsd)),
        BrainPlaneUsd = SumOrNull(scored.Select(r => r.BrainPlaneUsd)),
    };

    /// <summary>Sum only the PRICED values; null when none were priceable — a real $0 and "nothing could be priced" must not read the same.</summary>
    private static decimal? SumOrNull(IEnumerable<decimal?> values)
    {
        var priced = values.Where(v => v is not null).Select(v => v!.Value).ToList();

        return priced.Count == 0 ? null : priced.Sum();
    }

    /// <summary>One persisted row reduced to what the trend folds over — a data noun kept public so the pure folds are directly unit-testable.</summary>
    public readonly record struct TrendRow(DateTimeOffset CompletedAt, bool Solved, bool Delivered, bool UnattendedSolvedWithDelivery, decimal? CostUsd, decimal? BrainPlaneUsd, string? LessonArm)
    {
        public ArmedRunScore ToArmedScore() => new() { LessonArm = LessonArm, Solved = Solved, Delivered = Delivered, UnattendedSolvedWithDelivery = UnattendedSolvedWithDelivery };
    }

    /// <summary>A run the rate cannot include: parked (<paramref name="Suspended"/>) or pre-protocol. <paramref name="At"/> is its CreatedDate for a park, its CompletedAt for a legacy terminal.</summary>
    public readonly record struct UnscoredRun(DateTimeOffset At, bool Suspended);
}
