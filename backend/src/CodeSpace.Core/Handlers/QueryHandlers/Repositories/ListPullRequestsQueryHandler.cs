using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListPullRequestsQueryHandler : IRequestHandler<ListPullRequestsQuery, IReadOnlyList<RemotePullRequest>>
{
    private readonly IPullRequestService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListPullRequestsQueryHandler(IPullRequestService service, ICurrentTeam currentTeam) { _service = service; _currentTeam = currentTeam; }

    public async Task<IReadOnlyList<RemotePullRequest>> Handle(ListPullRequestsQuery request, CancellationToken cancellationToken)
    {
        // The ceilings live on the query record itself; the handler holds the caller to them. A page past the last one the
        // list allows is empty: clamping it to the last page would hand back that page's rows again, as the answer for a page
        // further on, to a caller who pages until one comes back empty.
        if (request.Page > ListPullRequestsQuery.MaxPage) return [];

        var page = Math.Max(request.Page, 1);
        var perPage = Math.Clamp(request.PerPage, 1, ListPullRequestsQuery.MaxPerPage);
        return await _service.ListAsync(request.RepositoryId, _currentTeam.Id!.Value, request.State, page, perPage, cancellationToken).ConfigureAwait(false);
    }
}
