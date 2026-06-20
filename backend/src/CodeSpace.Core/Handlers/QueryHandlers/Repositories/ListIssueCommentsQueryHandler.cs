using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Issues;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListIssueCommentsQueryHandler : IRequestHandler<ListIssueCommentsQuery, IReadOnlyList<RemoteIssueComment>>
{
    private readonly IIssueService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListIssueCommentsQueryHandler(IIssueService service, ICurrentTeam currentTeam) { _service = service; _currentTeam = currentTeam; }

    public async Task<IReadOnlyList<RemoteIssueComment>> Handle(ListIssueCommentsQuery request, CancellationToken cancellationToken) =>
        await _service.ListCommentsAsync(request.RepositoryId, _currentTeam.Id!.Value, request.Number, cancellationToken).ConfigureAwait(false);
}
