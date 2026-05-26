using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListPullRequestChecksQueryHandler : IRequestHandler<ListPullRequestChecksQuery, IReadOnlyList<RemotePullRequestCheck>>
{
    private readonly IPullRequestService _service;

    public ListPullRequestChecksQueryHandler(IPullRequestService service) { _service = service; }

    public async Task<IReadOnlyList<RemotePullRequestCheck>> Handle(ListPullRequestChecksQuery request, CancellationToken cancellationToken) =>
        await _service.ListChecksAsync(request.RepositoryId, request.Number, cancellationToken).ConfigureAwait(false);
}
