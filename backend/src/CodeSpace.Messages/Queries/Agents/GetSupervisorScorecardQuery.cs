using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// The team's supervisor-run scorecard — the cross-run roll-up (avg decisions/replan rounds, overall ground-truth
/// spawn success, outcome distribution) plus recent per-run scores over the L4-core supervisor lane (PR-E E6). The
/// supervisor-lane sibling of <c>GetAgentScorecardQuery</c>: it turns "is the supervisor working" from an assertion
/// into numbers an operator can audit. Team-scoped — the team comes from <c>ICurrentTeam</c> (the X-Team-Id header),
/// never the wire (<see cref="IRequireTeamMembership"/>), so a caller can only ever score its own supervisor runs.
///
/// <para><see cref="Since"/> windows the runs to a trend horizon (on each run's first decision). When no supervisor
/// runs exist (e.g. the lane was never enabled), the scorecard comes back empty — a flag-OFF deployment sees nothing
/// new.</para>
/// </summary>
public sealed record GetSupervisorScorecardQuery : IQuery<SupervisorScorecard>, IRequireTeamMembership
{
    /// <summary>Only score supervisor runs whose first decision is at/after this instant (a trend horizon, e.g. last 7 days). Null = all of the team's history (capped to the recent N).</summary>
    public DateTimeOffset? Since { get; init; }
}
