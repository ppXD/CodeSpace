using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="IPrematureStopRateService.ComputeAsync"/>. Mirrors <c>GetTeamCostRollupQueryHandler</c>.</summary>
public sealed class GetPrematureStopRateQueryHandler : IRequestHandler<GetPrematureStopRateQuery, PrematureStopRateReport>
{
    private readonly IPrematureStopRateService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetPrematureStopRateQueryHandler(IPrematureStopRateService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<PrematureStopRateReport> Handle(GetPrematureStopRateQuery request, CancellationToken cancellationToken)
    {
        return await _service.ComputeAsync(_currentTeam.Id!.Value, request.Since, cancellationToken).ConfigureAwait(false);
    }
}
