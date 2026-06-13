using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetPullRequestQueryHandler : IRequestHandler<GetPullRequestQuery, RemotePullRequest>
{
    private readonly IPullRequestService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetPullRequestQueryHandler(IPullRequestService service, ICurrentTeam currentTeam) { _service = service; _currentTeam = currentTeam; }

    public async Task<RemotePullRequest> Handle(GetPullRequestQuery request, CancellationToken cancellationToken) =>
        await _service.GetAsync(request.RepositoryId, _currentTeam.Id!.Value, request.Number, cancellationToken).ConfigureAwait(false);
}
