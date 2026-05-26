using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListPullRequestCommitsQueryHandler : IRequestHandler<ListPullRequestCommitsQuery, IReadOnlyList<RemotePullRequestCommit>>
{
    private readonly IPullRequestService _service;

    public ListPullRequestCommitsQueryHandler(IPullRequestService service) { _service = service; }

    public async Task<IReadOnlyList<RemotePullRequestCommit>> Handle(ListPullRequestCommitsQuery request, CancellationToken cancellationToken) =>
        await _service.ListCommitsAsync(request.RepositoryId, request.Number, cancellationToken).ConfigureAwait(false);
}
