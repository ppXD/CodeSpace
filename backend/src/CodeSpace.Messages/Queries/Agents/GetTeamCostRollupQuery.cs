using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// The team's token + estimated-USD spend roll-up over a window (SOTA #4) — turns the captured-but-previously-dead
/// per-run TokenUsage into an auditable bill. Team-scoped: the team comes from <c>ICurrentTeam</c> (the X-Team-Id
/// header), never the wire (<see cref="IRequireTeamMembership"/>), so a caller only ever sees its own spend.
///
/// <para><see cref="Since"/> windows the runs to a horizon. The estimate is HONEST about coverage: runs with no
/// captured usage or an unpriceable model are surfaced as unknown-cost rather than silently undercounting.</para>
/// </summary>
public sealed record GetTeamCostRollupQuery : IQuery<TeamCostRollup>, IRequireTeamMembership
{
    /// <summary>Only sum agent runs created at/after this instant (a window, e.g. this billing month). Null = all of the team's history (the per-run breakdown is capped most-recent-first; the summed totals are not).</summary>
    public DateTimeOffset? Since { get; init; }
}
