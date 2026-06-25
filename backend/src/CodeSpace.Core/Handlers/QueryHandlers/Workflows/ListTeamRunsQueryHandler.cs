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

    public Task<RunPage> Handle(ListTeamRunsQuery request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        var filter = request.ToFilter();

        // A page number switches to OFFSET (numbered) pagination — the History list; otherwise keyset (the live feed).
        return request.Page is { } page
            ? _service.ListTeamRunsPageAsync(teamId, filter, page, request.Limit, cancellationToken)
            : _service.ListTeamRunsAsync(teamId, filter, request.Cursor, request.Limit, cancellationToken);
    }
}
