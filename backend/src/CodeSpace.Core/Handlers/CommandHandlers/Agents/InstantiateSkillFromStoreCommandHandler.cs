using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class InstantiateSkillFromStoreCommandHandler : IRequestHandler<InstantiateSkillFromStoreCommand, Guid>
{
    private readonly ISkillDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public InstantiateSkillFromStoreCommandHandler(ISkillDefinitionService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<Guid> Handle(InstantiateSkillFromStoreCommand request, CancellationToken cancellationToken) =>
        _service.InstantiateFromStoreAsync(_currentTeam.Id!.Value, request.SourceDefinitionId, _currentUser.Id!.Value, cancellationToken);
}
