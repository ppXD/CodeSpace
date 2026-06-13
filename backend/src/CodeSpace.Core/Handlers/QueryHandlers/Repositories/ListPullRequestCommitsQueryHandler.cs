using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListPullRequestCommitsQueryHandler : IRequestHandler<ListPullRequestCommitsQuery, IReadOnlyList<RemotePullRequestCommit>>
{
    private readonly IPullRequestService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListPullRequestCommitsQueryHandler(IPullRequestService service, ICurrentTeam currentTeam) { _service = service; _currentTeam = currentTeam; }

    public async Task<IReadOnlyList<RemotePullRequestCommit>> Handle(ListPullRequestCommitsQuery request, CancellationToken cancellationToken) =>
        await _service.ListCommitsAsync(request.RepositoryId, _currentTeam.Id!.Value, request.Number, cancellationToken).ConfigureAwait(false);
}
