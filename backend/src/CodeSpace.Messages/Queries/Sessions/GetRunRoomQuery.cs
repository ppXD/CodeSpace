using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Sessions;

/// <summary>
/// The backend-authored Session Room (the AI work transcript) for the session a run belongs to, focused on that run's
/// turn. Works for any run in the thread (a turn or one of its attempts). Team-scoped
/// (<see cref="IRequireTeamMembership"/>); a foreign / session-less / missing run is an indistinguishable not-found
/// (null → 404).
/// </summary>
public sealed record GetRunRoomQuery : IQuery<RoomView?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }
}
