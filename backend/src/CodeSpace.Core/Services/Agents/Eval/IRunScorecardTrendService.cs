using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Reads the north-star OVER TIME from the durable <c>run_scorecard</c> rows — the one question the live-computed
/// scorecard cannot answer, because it scores the most recent ≤100 runs and discards the result. Read-only, and it
/// reads the persisted table ONLY: a trend assembled half from a table and half from a live recomputation would
/// mix two populations under one line.
/// </summary>
public interface IRunScorecardTrendService
{
    /// <summary>The team's daily buckets + lesson-arm slices over the last <paramref name="days"/> UTC days (clamped 1..<c>GetScorecardTrendQuery.MaxDays</c>). Empty buckets when nothing is persisted in the window.</summary>
    Task<RunScorecardTrend> ComputeAsync(Guid teamId, int days, CancellationToken cancellationToken);
}
