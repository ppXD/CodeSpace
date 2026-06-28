using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Queries.Sessions;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Sessions;

public sealed class GetSessionDetailQueryHandler : IRequestHandler<GetSessionDetailQuery, SessionDetail?>
{
    private readonly ISessionReadService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetSessionDetailQueryHandler(ISessionReadService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<SessionDetail?> Handle(GetSessionDetailQuery request, CancellationToken cancellationToken) =>
        _service.GetDetailAsync(request.SessionId, _currentTeam.Id!.Value, cancellationToken);
}
