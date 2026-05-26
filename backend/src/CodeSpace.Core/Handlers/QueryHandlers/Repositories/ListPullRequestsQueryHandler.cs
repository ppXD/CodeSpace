using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListPullRequestsQueryHandler : IRequestHandler<ListPullRequestsQuery, IReadOnlyList<RemotePullRequest>>
{
    private readonly IPullRequestService _service;

    public ListPullRequestsQueryHandler(IPullRequestService service) { _service = service; }

    public async Task<IReadOnlyList<RemotePullRequest>> Handle(ListPullRequestsQuery request, CancellationToken cancellationToken)
    {
        // Defaults / clamps live on the query record itself (Page>=1, PerPage clamped in
        // the service call). Handler is dispatch-only per Rule 16.
        var page = request.Page < 1 ? 1 : request.Page;
        var perPage = Math.Clamp(request.PerPage, 1, ListPullRequestsQuery.MaxPerPage);
        return await _service.ListAsync(request.RepositoryId, request.State, page, perPage, cancellationToken).ConfigureAwait(false);
    }
}
