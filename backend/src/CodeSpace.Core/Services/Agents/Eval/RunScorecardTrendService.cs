using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Folds a team's durable <c>run_scorecard</c> rows into daily buckets + lesson-arm slices. Thin (Rule 16) — the
/// service owns the team-scoped windowed query; the bucketing and the by-arm arithmetic are pure statics
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

        return new RunScorecardTrend
        {
            Since = since,
            ScoredRuns = rows.Count,
            Buckets = Bucket(rows),
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
    /// One bucket per UTC day that actually HAS runs, oldest first. A day with no terminal runs is ABSENT rather
    /// than a zero-rate point — "we solved nothing that day" and "nothing ran that day" are different claims, and
    /// only one of them belongs on a rate line. Pure so the bucketing unit-tests without a database.
    /// </summary>
    public static IReadOnlyList<RunScorecardTrendBucket> Bucket(IReadOnlyList<TrendRow> rows) =>
        rows
            .GroupBy(r => new DateTimeOffset(r.CompletedAt.UtcDateTime.Date, TimeSpan.Zero))
            .OrderBy(g => g.Key)
            .Select(g => new RunScorecardTrendBucket
            {
                Day = g.Key,
                Runs = g.Count(),
                SolvedRuns = g.Count(r => r.Solved),
                DeliveredRuns = g.Count(r => r.Delivered),
                UnattendedSolvedWithDeliveryRuns = g.Count(r => r.UnattendedSolvedWithDelivery),
                UnattendedSolveWithDeliveryRate = (double)g.Count(r => r.UnattendedSolvedWithDelivery) / g.Count(),
                CostUsd = SumOrNull(g.Select(r => r.CostUsd)),
                BrainPlaneUsd = SumOrNull(g.Select(r => r.BrainPlaneUsd)),
            })
            .ToList();

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
}
