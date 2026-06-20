using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Issues;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetIssueCountsQueryHandler : IRequestHandler<GetIssueCountsQuery, RemoteIssueCounts>
{
    private readonly IIssueService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetIssueCountsQueryHandler(IIssueService service, ICurrentTeam currentTeam) { _service = service; _currentTeam = currentTeam; }

    public async Task<RemoteIssueCounts> Handle(GetIssueCountsQuery request, CancellationToken cancellationToken) =>
        await _service.GetCountsAsync(request.RepositoryId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
