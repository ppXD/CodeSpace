using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Messages.Dtos.Variables;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Variables;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Variables;

public sealed class ListWorkflowVariablesQueryHandler : IRequestHandler<ListWorkflowVariablesQuery, IReadOnlyList<VariableSummary>>
{
    private readonly IVariableService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListWorkflowVariablesQueryHandler(IVariableService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<VariableSummary>> Handle(ListWorkflowVariablesQuery request, CancellationToken cancellationToken)
    {
        return await _service.ListAsync(VariableScope.Workflow, request.WorkflowId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
    }
}
