using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetPullRequestQueryHandler : IRequestHandler<GetPullRequestQuery, RemotePullRequest>
{
    private readonly IPullRequestService _service;

    public GetPullRequestQueryHandler(IPullRequestService service) { _service = service; }

    public async Task<RemotePullRequest> Handle(GetPullRequestQuery request, CancellationToken cancellationToken) =>
        await _service.GetAsync(request.RepositoryId, request.Number, cancellationToken).ConfigureAwait(false);
}
