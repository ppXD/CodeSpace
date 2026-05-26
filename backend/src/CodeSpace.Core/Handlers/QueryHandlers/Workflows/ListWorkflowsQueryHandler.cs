using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class ListWorkflowsQueryHandler : IRequestHandler<ListWorkflowsQuery, IReadOnlyList<WorkflowSummary>>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListWorkflowsQueryHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<IReadOnlyList<WorkflowSummary>> Handle(ListWorkflowsQuery request, CancellationToken cancellationToken) =>
        _service.ListAsync(_currentTeam.Id!.Value, cancellationToken);
}
