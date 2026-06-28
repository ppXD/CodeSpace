using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class SetAgentSkillsCommandHandler : IRequestHandler<SetAgentSkillsCommand, Unit>
{
    private readonly IAgentSkillBindingService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public SetAgentSkillsCommandHandler(IAgentSkillBindingService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Unit> Handle(SetAgentSkillsCommand request, CancellationToken cancellationToken)
    {
        await _service.SetForAgentAsync(_currentTeam.Id!.Value, request.AgentDefinitionId, request.SkillDefinitionIds, _currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
