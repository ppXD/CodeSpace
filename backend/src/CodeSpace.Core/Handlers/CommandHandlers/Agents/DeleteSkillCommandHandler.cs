using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class DeleteSkillCommandHandler : IRequestHandler<DeleteSkillCommand, Unit>
{
    private readonly ISkillDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public DeleteSkillCommandHandler(ISkillDefinitionService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Unit> Handle(DeleteSkillCommand request, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(_currentTeam.Id!.Value, request.SkillDefinitionId, _currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
