using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Computes a per-agent <see cref="AgentStatsRollup"/> over a team's agent-run history — the per-persona
/// success/latency/spend evidence the redesigned Agents roster reads. Team-scoped (multi-tenant isolation);
/// optionally windowed to a trend horizon. The per-harness/overall <see cref="IAgentRunScorecardService"/> answers
/// "which harness works"; this answers "which of MY agents is working".
/// </summary>
public interface IAgentStatsService
{
    Task<AgentStatsRollup> ComputeAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken);
}
