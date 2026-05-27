using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Messages.Commands.Variables;
using CodeSpace.Messages.Enums;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Variables;

public sealed class DeleteProjectVariableCommandHandler : IRequestHandler<DeleteProjectVariableCommand, Unit>
{
    private readonly IVariableService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public DeleteProjectVariableCommandHandler(IVariableService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Unit> Handle(DeleteProjectVariableCommand request, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(VariableScope.Project, request.ProjectId, _currentTeam.Id!.Value, request.Name, _currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
