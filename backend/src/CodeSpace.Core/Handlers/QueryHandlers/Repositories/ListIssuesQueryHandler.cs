using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Issues;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListIssuesQueryHandler : IRequestHandler<ListIssuesQuery, IReadOnlyList<RemoteIssue>>
{
    private readonly IIssueService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListIssuesQueryHandler(IIssueService service, ICurrentTeam currentTeam) { _service = service; _currentTeam = currentTeam; }

    public async Task<IReadOnlyList<RemoteIssue>> Handle(ListIssuesQuery request, CancellationToken cancellationToken)
    {
        // Same clamp discipline as ListPullRequestsQueryHandler — handler is dispatch-only per Rule 16.
        var page = request.Page < 1 ? 1 : request.Page;
        var perPage = Math.Clamp(request.PerPage, 1, ListIssuesQuery.MaxPerPage);
        return await _service.ListAsync(request.RepositoryId, _currentTeam.Id!.Value, request.State, page, perPage, cancellationToken).ConfigureAwait(false);
    }
}
