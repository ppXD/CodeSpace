using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// The team's unattended-delivery scorecard — the path-to-intelligence north-star: "task in → merged/published
/// artifact out with zero human touches," measured over every terminal run (single-agent or
/// supervisor-orchestrated alike). The generic sibling of <c>GetSupervisorScorecardQuery</c>. Team-scoped — the
/// team comes from <c>ICurrentTeam</c> (the X-Team-Id header), never the wire, so a caller can only ever score its
/// own runs.
///
/// <para><see cref="Since"/> windows the runs to a trend horizon (on each run's <c>CreatedDate</c>). When no
/// terminal runs exist yet, the scorecard comes back empty.</para>
/// </summary>
public sealed record GetUnattendedDeliveryScorecardQuery : IQuery<UnattendedDeliveryScorecard>, IRequireTeamMembership
{
    /// <summary>Only score runs created at/after this instant (a trend horizon, e.g. last 7 days). Null = all of the team's history (capped to the recent N).</summary>
    public DateTimeOffset? Since { get; init; }
}
