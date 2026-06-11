using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class GetAgentRunQueryHandler : IRequestHandler<GetAgentRunQuery, AgentRunSummary?>
{
    private readonly IAgentRunService _runs;
    private readonly ICurrentTeam _currentTeam;

    public GetAgentRunQueryHandler(IAgentRunService runs, ICurrentTeam currentTeam)
    {
        _runs = runs;
        _currentTeam = currentTeam;
    }

    public Task<AgentRunSummary?> Handle(GetAgentRunQuery request, CancellationToken cancellationToken) =>
        _runs.GetSummaryForTeamAsync(request.AgentRunId, _currentTeam.Id!.Value, cancellationToken);
}
