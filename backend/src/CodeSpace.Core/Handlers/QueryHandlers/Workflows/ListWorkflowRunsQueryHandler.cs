using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class ListWorkflowRunsQueryHandler : IRequestHandler<ListWorkflowRunsQuery, IReadOnlyList<WorkflowRunSummary>>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListWorkflowRunsQueryHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<IReadOnlyList<WorkflowRunSummary>> Handle(ListWorkflowRunsQuery request, CancellationToken cancellationToken) =>
        _service.ListRunsAsync(request.WorkflowId, _currentTeam.Id!.Value, request.Limit, cancellationToken);
}
