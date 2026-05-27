using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Projects;
using CodeSpace.Messages.Commands.Projects;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Projects;

public sealed class MoveRepositoryToProjectCommandHandler : IRequestHandler<MoveRepositoryToProjectCommand, Unit>
{
    private readonly IProjectService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public MoveRepositoryToProjectCommandHandler(IProjectService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Unit> Handle(MoveRepositoryToProjectCommand request, CancellationToken cancellationToken)
    {
        await _service.MoveRepositoryAsync(_currentTeam.Id!.Value, request.RepositoryId, request.TargetProjectId, _currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
