using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

/// <summary>
/// Thin dispatcher (Rule 16) — the production caller of <see cref="IUnattendedDeliveryScorecardService.ComputeAsync"/>.
/// It scopes the score to the CALLER'S team (<see cref="ICurrentTeam"/>, never the wire), threads the optional
/// since filter straight through, and returns the scorer's <see cref="UnattendedDeliveryScorecard"/> as-is.
/// Mirrors <c>GetSupervisorScorecardQueryHandler</c>.
/// </summary>
public sealed class GetUnattendedDeliveryScorecardQueryHandler : IRequestHandler<GetUnattendedDeliveryScorecardQuery, UnattendedDeliveryScorecard>
{
    private readonly IUnattendedDeliveryScorecardService _scorecards;
    private readonly ICurrentTeam _currentTeam;

    public GetUnattendedDeliveryScorecardQueryHandler(IUnattendedDeliveryScorecardService scorecards, ICurrentTeam currentTeam)
    {
        _scorecards = scorecards;
        _currentTeam = currentTeam;
    }

    public async Task<UnattendedDeliveryScorecard> Handle(GetUnattendedDeliveryScorecardQuery request, CancellationToken cancellationToken)
    {
        return await _scorecards.ComputeAsync(_currentTeam.Id!.Value, request.Since, cancellationToken).ConfigureAwait(false);
    }
}
