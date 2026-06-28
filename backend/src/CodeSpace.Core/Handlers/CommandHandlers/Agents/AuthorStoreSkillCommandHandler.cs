using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class AuthorStoreSkillCommandHandler : IRequestHandler<AuthorStoreSkillCommand, Guid>
{
    private readonly ISkillDefinitionService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public AuthorStoreSkillCommandHandler(ISkillDefinitionService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<Guid> Handle(AuthorStoreSkillCommand request, CancellationToken cancellationToken) =>
        _service.AuthorStoreSkillAsync(_currentTeam.Id!.Value, ToInput(request), _currentUser.Id!.Value, cancellationToken);

    private static SkillDefinitionInput ToInput(AuthorStoreSkillCommand r) => new()
    {
        Name = r.Name,
        Description = r.Description,
        Body = r.Body,
        Category = r.Category,
    };
}
