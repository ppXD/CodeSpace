using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Issues;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetIssueQueryHandler : IRequestHandler<GetIssueQuery, RemoteIssue>
{
    private readonly IIssueService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetIssueQueryHandler(IIssueService service, ICurrentTeam currentTeam) { _service = service; _currentTeam = currentTeam; }

    public async Task<RemoteIssue> Handle(GetIssueQuery request, CancellationToken cancellationToken) =>
        await _service.GetAsync(request.RepositoryId, _currentTeam.Id!.Value, request.Number, cancellationToken).ConfigureAwait(false);
}
