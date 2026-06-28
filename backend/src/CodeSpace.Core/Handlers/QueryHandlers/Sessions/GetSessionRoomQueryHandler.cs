using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Queries.Sessions;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Sessions;

public sealed class GetSessionRoomQueryHandler : IRequestHandler<GetSessionRoomQuery, RoomView?>
{
    private readonly IRoomProjector _projector;
    private readonly ICurrentTeam _currentTeam;

    public GetSessionRoomQueryHandler(IRoomProjector projector, ICurrentTeam currentTeam)
    {
        _projector = projector;
        _currentTeam = currentTeam;
    }

    public Task<RoomView?> Handle(GetSessionRoomQuery request, CancellationToken cancellationToken) =>
        _projector.ProjectAsync(request.SessionId, request.FocusRunId, _currentTeam.Id!.Value, cancellationToken);
}
