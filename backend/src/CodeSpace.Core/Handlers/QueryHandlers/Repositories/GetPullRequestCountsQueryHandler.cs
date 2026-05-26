using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetPullRequestCountsQueryHandler : IRequestHandler<GetPullRequestCountsQuery, RemotePullRequestCounts>
{
    private readonly IPullRequestService _service;

    public GetPullRequestCountsQueryHandler(IPullRequestService service) { _service = service; }

    public async Task<RemotePullRequestCounts> Handle(GetPullRequestCountsQuery request, CancellationToken cancellationToken) =>
        await _service.GetCountsAsync(request.RepositoryId, cancellationToken).ConfigureAwait(false);
}
