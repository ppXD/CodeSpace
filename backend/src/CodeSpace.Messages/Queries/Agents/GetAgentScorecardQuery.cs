using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// The team's agent-run scorecard — per-harness + overall success rate and latency (P50/P95) over the
/// team's terminal agent runs. The measurement spine: it turns "is the agent working" from an assertion
/// into a number an operator can audit. Team-scoped: the team comes from <c>ICurrentTeam</c> (the
/// X-Team-Id header), never the wire (<see cref="IRequireTeamMembership"/>), so a caller can only ever
/// score its own runs — the multi-tenant promise.
///
/// <para><see cref="Since"/> windows the runs to a trend horizon; <see cref="Harness"/> narrows to one
/// harness. Both are the optional filters <c>IAgentRunScorecardService.ComputeAsync</c> already supports —
/// they are exposed here unchanged, not invented.</para>
/// </summary>
public sealed record GetAgentScorecardQuery : IQuery<AgentRunScorecard>, IRequireTeamMembership
{
    /// <summary>Only score runs created at/after this instant (a trend horizon, e.g. last 7 days). Null = all of the team's history.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only score this harness kind (e.g. "codex-cli"). Null/blank = every harness (with the per-harness breakdown + overall rollup).</summary>
    public string? Harness { get; init; }
}
