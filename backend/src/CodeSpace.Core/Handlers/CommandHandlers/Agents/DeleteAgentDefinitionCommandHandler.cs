using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class DeleteAgentDefinitionCommandHandler : IRequestHandler<DeleteAgentDefinitionCommand, Unit>
{
    private readonly IAgentDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public DeleteAgentDefinitionCommandHandler(IAgentDefinitionService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Unit> Handle(DeleteAgentDefinitionCommand request, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(_currentTeam.Id!.Value, request.AgentDefinitionId, _currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
