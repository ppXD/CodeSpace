using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Queries.Sessions;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Sessions;

public sealed class GetSessionByRunQueryHandler : IRequestHandler<GetSessionByRunQuery, SessionDetail?>
{
    private readonly ISessionReadService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetSessionByRunQueryHandler(ISessionReadService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<SessionDetail?> Handle(GetSessionByRunQuery request, CancellationToken cancellationToken) =>
        _service.GetByRunAsync(request.RunId, _currentTeam.Id!.Value, cancellationToken);
}
