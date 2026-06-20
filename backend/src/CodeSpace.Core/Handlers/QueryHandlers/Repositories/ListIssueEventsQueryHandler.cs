using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Issues;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListIssueEventsQueryHandler : IRequestHandler<ListIssueEventsQuery, IReadOnlyList<RemoteIssueEvent>>
{
    private readonly IIssueService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListIssueEventsQueryHandler(IIssueService service, ICurrentTeam currentTeam) { _service = service; _currentTeam = currentTeam; }

    public async Task<IReadOnlyList<RemoteIssueEvent>> Handle(ListIssueEventsQuery request, CancellationToken cancellationToken) =>
        await _service.ListEventsAsync(request.RepositoryId, _currentTeam.Id!.Value, request.Number, cancellationToken).ConfigureAwait(false);
}
