using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class UpdateAgentDefinitionCommandHandler : IRequestHandler<UpdateAgentDefinitionCommand, Unit>
{
    private readonly IAgentDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public UpdateAgentDefinitionCommandHandler(IAgentDefinitionService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Unit> Handle(UpdateAgentDefinitionCommand request, CancellationToken cancellationToken)
    {
        await _service.UpdateAsync(_currentTeam.Id!.Value, request.AgentDefinitionId, ToInput(request), _currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }

    private static AgentDefinitionInput ToInput(UpdateAgentDefinitionCommand r) => new()
    {
        Name = r.Name,
        Description = r.Description,
        SystemPrompt = r.SystemPrompt,
        Model = r.Model,
        DefaultAutonomy = r.DefaultAutonomy,
        Tools = r.Tools,
    };
}
