using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Computes an <see cref="AgentRunScorecard"/> over a team's agent-run history — the success/latency view that
/// makes agent quality measurable instead of asserted. Team-scoped (multi-tenant isolation); optionally filtered
/// to a time window and/or a single harness so an operator can compare harnesses or watch a trend.
/// </summary>
public interface IAgentRunScorecardService
{
    Task<AgentRunScorecard> ComputeAsync(Guid teamId, DateTimeOffset? since, string? harness, CancellationToken cancellationToken);
}
