using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Sessions;

/// <summary>
/// A generic preview of ONE file a turn produced, resolved from the producing agent's captured diff. Keyed by the
/// turn's run id + the repo-relative path (works for any run in the thread — a turn or an attempt). Team-scoped
/// (<see cref="IRequireTeamMembership"/>); a foreign / missing run is an indistinguishable not-found (null → 404).
/// </summary>
public sealed record GetSessionRoomFileQuery : IQuery<RoomFilePreview?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }

    public string Path { get; init; } = "";
}
