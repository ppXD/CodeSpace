using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class AuthorStoreAgentCommandHandler : IRequestHandler<AuthorStoreAgentCommand, Guid>
{
    private readonly IAgentDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public AuthorStoreAgentCommandHandler(IAgentDefinitionService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<Guid> Handle(AuthorStoreAgentCommand request, CancellationToken cancellationToken) =>
        _service.AuthorStoreAgentAsync(_currentTeam.Id!.Value, ToInput(request), _currentUser.Id!.Value, cancellationToken);

    private static AgentDefinitionInput ToInput(AuthorStoreAgentCommand r) => new()
    {
        Name = r.Name,
        Description = r.Description,
        SystemPrompt = r.SystemPrompt,
        Model = r.Model,
        DefaultAutonomy = r.DefaultAutonomy,
        Tools = r.Tools,
    };
}
