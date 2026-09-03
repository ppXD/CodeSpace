using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

/// <summary>Thin dispatcher (Rule 16) — scopes the trend to the CALLER'S team (<see cref="ICurrentTeam"/>, never the wire) and returns the service's answer as-is.</summary>
public sealed class GetScorecardTrendQueryHandler : IRequestHandler<GetScorecardTrendQuery, RunScorecardTrend>
{
    private readonly IRunScorecardTrendService _trends;
    private readonly ICurrentTeam _currentTeam;

    public GetScorecardTrendQueryHandler(IRunScorecardTrendService trends, ICurrentTeam currentTeam)
    {
        _trends = trends;
        _currentTeam = currentTeam;
    }

    public async Task<RunScorecardTrend> Handle(GetScorecardTrendQuery request, CancellationToken cancellationToken)
    {
        return await _trends.ComputeAsync(_currentTeam.Id!.Value, request.Days, cancellationToken).ConfigureAwait(false);
    }
}
