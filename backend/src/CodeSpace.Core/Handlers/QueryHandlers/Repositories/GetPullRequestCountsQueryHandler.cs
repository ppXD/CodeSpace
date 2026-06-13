using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetPullRequestCountsQueryHandler : IRequestHandler<GetPullRequestCountsQuery, RemotePullRequestCounts>
{
    private readonly IPullRequestService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetPullRequestCountsQueryHandler(IPullRequestService service, ICurrentTeam currentTeam) { _service = service; _currentTeam = currentTeam; }

    public async Task<RemotePullRequestCounts> Handle(GetPullRequestCountsQuery request, CancellationToken cancellationToken) =>
        await _service.GetCountsAsync(request.RepositoryId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
