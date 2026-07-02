using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// Per-agent run stats over the team's agent-run history — one <see cref="AgentStat"/> per persona that has runs,
/// with its recent-outcome sparkline, windowed success rate, latency, and spend. The evidence the redesigned Agents
/// roster shows on each row. Team-scoped: the team comes from <c>ICurrentTeam</c> (the X-Team-Id header), never the
/// wire (<see cref="IRequireTeamMembership"/>), so a caller only ever sees its own personas' runs.
///
/// <para><see cref="Since"/> windows the runs to a trend horizon (e.g. last 7 days). It is the same optional filter
/// <c>IAgentStatsService.ComputeAsync</c> supports — exposed here unchanged, so the roster's time-window control feeds
/// a filter the backend honours, mirroring the scorecard's own <c>since</c> window.</para>
/// </summary>
public sealed record GetAgentStatsQuery : IQuery<AgentStatsRollup>, IRequireTeamMembership
{
    /// <summary>Only count runs created at/after this instant (a trend horizon, e.g. last 7 days). Null = all of the team's history.</summary>
    public DateTimeOffset? Since { get; init; }
}
