using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListPullRequestFilesQueryHandler : IRequestHandler<ListPullRequestFilesQuery, IReadOnlyList<RemotePullRequestFile>>
{
    private readonly IPullRequestService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListPullRequestFilesQueryHandler(IPullRequestService service, ICurrentTeam currentTeam) { _service = service; _currentTeam = currentTeam; }

    public async Task<IReadOnlyList<RemotePullRequestFile>> Handle(ListPullRequestFilesQuery request, CancellationToken cancellationToken) =>
        await _service.ListFilesAsync(request.RepositoryId, _currentTeam.Id!.Value, request.Number, cancellationToken).ConfigureAwait(false);
}
