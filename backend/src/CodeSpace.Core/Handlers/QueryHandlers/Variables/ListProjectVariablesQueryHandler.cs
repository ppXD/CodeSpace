using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Messages.Dtos.Variables;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Variables;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Variables;

public sealed class ListProjectVariablesQueryHandler : IRequestHandler<ListProjectVariablesQuery, IReadOnlyList<VariableSummary>>
{
    private readonly IVariableService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListProjectVariablesQueryHandler(IVariableService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<IReadOnlyList<VariableSummary>> Handle(ListProjectVariablesQuery request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        return _service.ListAsync(VariableScope.Project, request.ProjectId, teamId, cancellationToken);
    }
}
