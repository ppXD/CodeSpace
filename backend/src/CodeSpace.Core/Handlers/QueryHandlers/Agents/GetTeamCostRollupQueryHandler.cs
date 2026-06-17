using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

/// <summary>
/// Thin dispatcher (Rule 16) — the production caller of <see cref="ITeamCostService.ComputeRollupAsync"/>. Scopes
/// the roll-up to the CALLER'S team (<see cref="ICurrentTeam"/>, never the wire), threads the optional since window
/// straight through, and returns the service's <see cref="TeamCostRollup"/> as-is (already a Messages contract).
/// Mirrors <c>GetSupervisorScorecardQueryHandler</c>.
/// </summary>
public sealed class GetTeamCostRollupQueryHandler : IRequestHandler<GetTeamCostRollupQuery, TeamCostRollup>
{
    private readonly ITeamCostService _cost;
    private readonly ICurrentTeam _currentTeam;

    public GetTeamCostRollupQueryHandler(ITeamCostService cost, ICurrentTeam currentTeam)
    {
        _cost = cost;
        _currentTeam = currentTeam;
    }

    public async Task<TeamCostRollup> Handle(GetTeamCostRollupQuery request, CancellationToken cancellationToken)
    {
        return await _cost.ComputeRollupAsync(_currentTeam.Id!.Value, request.Since, cancellationToken).ConfigureAwait(false);
    }
}
