using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class CreateAgentDefinitionCommandHandler : IRequestHandler<CreateAgentDefinitionCommand, Guid>
{
    private readonly IAgentDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public CreateAgentDefinitionCommandHandler(IAgentDefinitionService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<Guid> Handle(CreateAgentDefinitionCommand request, CancellationToken cancellationToken) =>
        _service.CreateAsync(_currentTeam.Id!.Value, ToInput(request), _currentUser.Id!.Value, cancellationToken);

    private static AgentDefinitionInput ToInput(CreateAgentDefinitionCommand r) => new()
    {
        Name = r.Name,
        Description = r.Description,
        SystemPrompt = r.SystemPrompt,
        Model = r.Model,
        DefaultAutonomy = r.DefaultAutonomy,
        Tools = r.Tools,
    };
}
