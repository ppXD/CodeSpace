using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Messages.Dtos.Variables;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Variables;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Variables;

public sealed class ListTeamVariablesQueryHandler : IRequestHandler<ListTeamVariablesQuery, IReadOnlyList<VariableSummary>>
{
    private readonly IVariableService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListTeamVariablesQueryHandler(IVariableService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<VariableSummary>> Handle(ListTeamVariablesQuery request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        return await _service.ListAsync(VariableScope.Team, teamId, teamId, cancellationToken).ConfigureAwait(false);
    }
}
