using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class GetAgentDefinitionQueryHandler : IRequestHandler<GetAgentDefinitionQuery, AgentDefinitionSummary?>
{
    private readonly IAgentDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetAgentDefinitionQueryHandler(IAgentDefinitionService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<AgentDefinitionSummary?> Handle(GetAgentDefinitionQuery request, CancellationToken cancellationToken) =>
        _service.GetAsync(_currentTeam.Id!.Value, request.AgentDefinitionId, cancellationToken);
}
