using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// P4 — the team's premature-stop-rate report: of the task runs it started (single-agent, plan-map, supervisor
/// alike), what fraction died prematurely rather than reaching a genuine conclusion — the stability north-star.
/// Team-scoped: the team comes from <c>ICurrentTeam</c> (the X-Team-Id header), never the wire.
/// </summary>
public sealed record GetPrematureStopRateQuery : IQuery<PrematureStopRateReport>, IRequireTeamMembership
{
    /// <summary>Only count runs created at/after this instant (a window, e.g. the last 30 days). Null = the team's full history.</summary>
    public DateTimeOffset? Since { get; init; }
}
