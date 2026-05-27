using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Projects;
using CodeSpace.Messages.Commands.Projects;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Projects;

public sealed class CreateProjectCommandHandler : IRequestHandler<CreateProjectCommand, Guid>
{
    private readonly IProjectService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public CreateProjectCommandHandler(IProjectService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<Guid> Handle(CreateProjectCommand request, CancellationToken cancellationToken) =>
        _service.CreateAsync(_currentTeam.Id!.Value, request.Name, request.Description, _currentUser.Id!.Value, cancellationToken);
}
