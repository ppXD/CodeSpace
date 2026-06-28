using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Sessions;

/// <summary>
/// The backend-authored Session Room for a session, focused on <see cref="FocusRunId"/>'s turn when given (else the
/// latest turn). Team-scoped (<see cref="IRequireTeamMembership"/>); a foreign / missing session is an
/// indistinguishable not-found (null → 404).
/// </summary>
public sealed record GetSessionRoomQuery : IQuery<RoomView?>, IRequireTeamMembership
{
    public Guid SessionId { get; init; }

    public Guid? FocusRunId { get; init; }
}
