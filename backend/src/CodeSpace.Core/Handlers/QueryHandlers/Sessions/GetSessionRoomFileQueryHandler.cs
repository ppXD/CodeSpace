using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Queries.Sessions;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Sessions;

public sealed class GetSessionRoomFileQueryHandler : IRequestHandler<GetSessionRoomFileQuery, RoomFilePreview?>
{
    private readonly IRoomFilePreviewService _preview;
    private readonly ICurrentTeam _currentTeam;

    public GetSessionRoomFileQueryHandler(IRoomFilePreviewService preview, ICurrentTeam currentTeam)
    {
        _preview = preview;
        _currentTeam = currentTeam;
    }

    public Task<RoomFilePreview?> Handle(GetSessionRoomFileQuery request, CancellationToken cancellationToken) =>
        _preview.PreviewAsync(request.RunId, request.Path, _currentTeam.Id!.Value, cancellationToken);
}
