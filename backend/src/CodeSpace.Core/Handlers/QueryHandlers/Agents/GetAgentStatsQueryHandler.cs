using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

/// <summary>
/// Thin dispatcher (Rule 16) — the production caller of <see cref="IAgentStatsService.ComputeAsync"/>. Scopes the
/// stats to the CALLER'S team (<see cref="ICurrentTeam"/>, never the wire), threads the optional since window
/// straight through, and returns the service's <see cref="AgentStatsRollup"/> as-is (already a Messages contract the
/// API/UI consume). Mirrors <c>GetAgentScorecardQueryHandler</c>.
/// </summary>
public sealed class GetAgentStatsQueryHandler : IRequestHandler<GetAgentStatsQuery, AgentStatsRollup>
{
    private readonly IAgentStatsService _stats;
    private readonly ICurrentTeam _currentTeam;

    public GetAgentStatsQueryHandler(IAgentStatsService stats, ICurrentTeam currentTeam)
    {
        _stats = stats;
        _currentTeam = currentTeam;
    }

    public async Task<AgentStatsRollup> Handle(GetAgentStatsQuery request, CancellationToken cancellationToken)
    {
        return await _stats.ComputeAsync(_currentTeam.Id!.Value, request.Since, cancellationToken).ConfigureAwait(false);
    }
}
