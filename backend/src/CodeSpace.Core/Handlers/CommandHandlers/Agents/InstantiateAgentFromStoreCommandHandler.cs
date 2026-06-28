using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class InstantiateAgentFromStoreCommandHandler : IRequestHandler<InstantiateAgentFromStoreCommand, Guid>
{
    private readonly IAgentDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public InstantiateAgentFromStoreCommandHandler(IAgentDefinitionService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<Guid> Handle(InstantiateAgentFromStoreCommand request, CancellationToken cancellationToken) =>
        _service.InstantiateFromStoreAsync(_currentTeam.Id!.Value, request.SourceDefinitionId, _currentUser.Id!.Value, cancellationToken);
}
