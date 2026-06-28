using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Queries.Sessions;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Sessions;

public sealed class GetRunRoomQueryHandler : IRequestHandler<GetRunRoomQuery, RoomView?>
{
    private readonly IRoomProjector _projector;
    private readonly ICurrentTeam _currentTeam;

    public GetRunRoomQueryHandler(IRoomProjector projector, ICurrentTeam currentTeam)
    {
        _projector = projector;
        _currentTeam = currentTeam;
    }

    public Task<RoomView?> Handle(GetRunRoomQuery request, CancellationToken cancellationToken) =>
        _projector.ProjectByRunAsync(request.RunId, _currentTeam.Id!.Value, cancellationToken);
}
