using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class ListAgentDefinitionsQueryHandler : IRequestHandler<ListAgentDefinitionsQuery, IReadOnlyList<AgentDefinitionSummary>>
{
    private readonly IAgentDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListAgentDefinitionsQueryHandler(IAgentDefinitionService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<IReadOnlyList<AgentDefinitionSummary>> Handle(ListAgentDefinitionsQuery request, CancellationToken cancellationToken) =>
        _service.ListAsync(_currentTeam.Id!.Value, cancellationToken);
}
