using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Queries.Sessions;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Sessions;

public sealed class ListTeamSessionsQueryHandler : IRequestHandler<ListTeamSessionsQuery, SessionPage>
{
    private readonly ISessionReadService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListTeamSessionsQueryHandler(ISessionReadService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<SessionPage> Handle(ListTeamSessionsQuery request, CancellationToken cancellationToken) =>
        _service.ListAsync(_currentTeam.Id!.Value, request.Cursor, request.Limit, cancellationToken);
}
