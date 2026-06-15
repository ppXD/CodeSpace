using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

/// <summary>
/// Thin dispatcher (Rule 16) — the FIRST production caller of <see cref="IAgentRunScorecardService.ComputeAsync"/>.
/// It scopes the score to the CALLER'S team (<see cref="ICurrentTeam"/>, never the wire), threads the optional
/// since/harness filters straight through, and returns the scorer's <see cref="AgentRunScorecard"/> as-is — the
/// scorecard is already a Messages contract the API/UI consume, so re-projecting would only add drift.
/// </summary>
public sealed class GetAgentScorecardQueryHandler : IRequestHandler<GetAgentScorecardQuery, AgentRunScorecard>
{
    private readonly IAgentRunScorecardService _scorecards;
    private readonly ICurrentTeam _currentTeam;

    public GetAgentScorecardQueryHandler(IAgentRunScorecardService scorecards, ICurrentTeam currentTeam)
    {
        _scorecards = scorecards;
        _currentTeam = currentTeam;
    }

    public async Task<AgentRunScorecard> Handle(GetAgentScorecardQuery request, CancellationToken cancellationToken)
    {
        return await _scorecards.ComputeAsync(_currentTeam.Id!.Value, request.Since, request.Harness, cancellationToken).ConfigureAwait(false);
    }
}
