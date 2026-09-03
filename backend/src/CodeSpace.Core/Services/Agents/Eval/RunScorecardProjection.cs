using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// The gathered facts one <c>run_scorecard</c> row is written from. A parameter object rather than seven
/// positional arguments, so the mapping below has a signature a reader can hold in their head — and so the mapping
/// itself is pure and unit-testable without a database.
/// </summary>
public sealed record RunScorecardFacts
{
    /// <summary>When the run reached its terminal (the writer falls back to last-modified for a bypass terminal that recorded none) — the trend's bucketing key.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    public string? ProjectionKind { get; init; }

    /// <summary>The run's score as produced by the ONE existing <see cref="UnattendedDeliveryScorer"/> — the headline bit is copied, never recomputed here.</summary>
    public required UnattendedDeliveryRunScore Score { get; init; }

    public decimal? BrainPlaneUsd { get; init; }
    public string? BrainModel { get; init; }
    public string? LessonArm { get; init; }
}

/// <summary>
/// Maps gathered facts onto a <see cref="RunScorecard"/> row. Pure (it mutates only the row handed to it) so the
/// solved × delivered × arm combinations are pinned exhaustively without Postgres.
///
/// <para>The headline bit is COPIED from the scorer's score, never re-derived — a second definition of "solved
/// with delivery" living here is exactly the drift the schema's own CHECK constraint and
/// <see cref="UnattendedDeliveryScorer.ScorerVersion"/> exist to prevent.</para>
/// </summary>
public static class RunScorecardProjection
{
    public static RunScorecard Apply(RunScorecard row, RunScorecardFacts facts)
    {
        row.CompletedAt = facts.CompletedAt;
        row.ProjectionKind = facts.ProjectionKind;
        row.Solved = facts.Score.Solved;
        row.Delivered = facts.Score.Delivered;
        row.HumanTouches = facts.Score.HumanTouches;
        row.UnattendedSolvedWithDelivery = facts.Score.UnattendedSolvedWithDelivery;
        row.CostUsd = facts.Score.CostUsd;
        row.BrainPlaneUsd = facts.BrainPlaneUsd;
        row.LessonArm = facts.LessonArm;
        row.BrainModel = facts.BrainModel;
        row.ScorerVersion = UnattendedDeliveryScorer.ScorerVersion;

        return row;
    }
}
