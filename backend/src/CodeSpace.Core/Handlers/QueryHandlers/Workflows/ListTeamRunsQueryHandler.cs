using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class ListTeamRunsQueryHandler : IRequestHandler<ListTeamRunsQuery, RunPage>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListTeamRunsQueryHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<RunPage> Handle(ListTeamRunsQuery request, CancellationToken cancellationToken) =>
        _service.ListTeamRunsAsync(_currentTeam.Id!.Value, request.ToFilter(), request.Cursor, request.Limit, cancellationToken);
}
