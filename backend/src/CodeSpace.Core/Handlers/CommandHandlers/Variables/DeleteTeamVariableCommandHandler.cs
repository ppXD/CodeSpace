using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Messages.Commands.Variables;
using CodeSpace.Messages.Enums;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Variables;

public sealed class DeleteTeamVariableCommandHandler : IRequestHandler<DeleteTeamVariableCommand, Unit>
{
    private readonly IVariableService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public DeleteTeamVariableCommandHandler(IVariableService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Unit> Handle(DeleteTeamVariableCommand request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        await _service.DeleteAsync(VariableScope.Team, teamId, teamId, request.Name, _currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
