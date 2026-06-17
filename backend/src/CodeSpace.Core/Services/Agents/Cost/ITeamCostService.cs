using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Cost;

/// <summary>
/// The team-scoped READ plane over the captured-but-previously-dead <c>AgentRunResult.TokenUsage</c> (SOTA #4):
/// sums each terminal agent run's tokens, prices them via <see cref="AgentCostPricing"/>, and rolls them up per run
/// and per team. Read-only + thin (Rule 16) — it owns only the team-scoped query + the pricing projection; the cost
/// math is the pure pricer's. Team-scoping is fail-closed (the query filters by teamId); an unprice­able run is
/// surfaced as unknown-cost (fail-open), never silently zeroed away.
/// </summary>
public interface ITeamCostService
{
    /// <summary>The team's cumulative token + estimated-USD spend over the window (null <paramref name="since"/> = all history); the summed totals cover every run in the window, the per-run list is most-recent-first and payload-bounded.</summary>
    Task<TeamCostRollup> ComputeRollupAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken);

    /// <summary>The token + estimated-USD spend of ONE run (team-scoped, fail-closed), summed over its terminal agent runs.</summary>
    Task<RunCostSummary> ComputeRunAsync(Guid teamId, Guid workflowRunId, CancellationToken cancellationToken);
}
