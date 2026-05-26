using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Messages.Commands.Variables;
using CodeSpace.Messages.Enums;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Variables;

public sealed class SetTeamVariableCommandHandler : IRequestHandler<SetTeamVariableCommand, Unit>
{
    private readonly IVariableService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public SetTeamVariableCommandHandler(IVariableService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Unit> Handle(SetTeamVariableCommand request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        await _service.SetAsync(VariableScope.Team, teamId, teamId, request.Name, request.ValueType, request.Value, request.Description, _currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
