using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListPullRequestChecksQueryHandler : IRequestHandler<ListPullRequestChecksQuery, IReadOnlyList<RemotePullRequestCheck>>
{
    private readonly IPullRequestService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListPullRequestChecksQueryHandler(IPullRequestService service, ICurrentTeam currentTeam) { _service = service; _currentTeam = currentTeam; }

    public async Task<IReadOnlyList<RemotePullRequestCheck>> Handle(ListPullRequestChecksQuery request, CancellationToken cancellationToken) =>
        await _service.ListChecksAsync(request.RepositoryId, _currentTeam.Id!.Value, request.Number, cancellationToken).ConfigureAwait(false);
}
