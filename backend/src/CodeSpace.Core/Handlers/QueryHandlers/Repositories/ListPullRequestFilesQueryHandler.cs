using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListPullRequestFilesQueryHandler : IRequestHandler<ListPullRequestFilesQuery, IReadOnlyList<RemotePullRequestFile>>
{
    private readonly IPullRequestService _service;

    public ListPullRequestFilesQueryHandler(IPullRequestService service) { _service = service; }

    public async Task<IReadOnlyList<RemotePullRequestFile>> Handle(ListPullRequestFilesQuery request, CancellationToken cancellationToken) =>
        await _service.ListFilesAsync(request.RepositoryId, request.Number, cancellationToken).ConfigureAwait(false);
}
