using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class ListTeamRunsQueryHandler : IRequestHandler<ListTeamRunsQuery, IReadOnlyList<WorkflowRunSummary>>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListTeamRunsQueryHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<IReadOnlyList<WorkflowRunSummary>> Handle(ListTeamRunsQuery request, CancellationToken cancellationToken) =>
        _service.ListTeamRunsAsync(_currentTeam.Id!.Value, request.Limit, cancellationToken);
}
