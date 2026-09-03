using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// A4: the north-star OVER TIME — daily buckets + the lesson-arm slices, read from the durable <c>run_scorecard</c>
/// rows. Team-scoped: the team comes from <c>ICurrentTeam</c> (the X-Team-Id header), never the wire, so a caller
/// can only ever read its own trend.
/// </summary>
public sealed record GetScorecardTrendQuery : IQuery<RunScorecardTrend>, IRequireTeamMembership
{
    /// <summary>The default horizon — four weeks, enough to see a week-over-week move without paging.</summary>
    public const int DefaultDays = 28;

    /// <summary>The hard ceiling on <see cref="Days"/> — the payload is one bucket per day, so the window is bounded rather than trusted.</summary>
    public const int MaxDays = 365;

    /// <summary>How many UTC days back to read, clamped to 1..<see cref="MaxDays"/> by the service.</summary>
    public int Days { get; init; } = DefaultDays;
}
