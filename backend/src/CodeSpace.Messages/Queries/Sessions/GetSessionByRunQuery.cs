using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Sessions;

/// <summary>
/// Resolve the session a run belongs to, as the full conversation anchored at that run's turn. Works for ANY run in the
/// thread — a top-level turn or one of its rerun attempts (which carry the session id with a null turn index) both
/// resolve to the same thread, with <c>AnchorTurnIndex</c> on the owning turn. Team-scoped
/// (<see cref="IRequireTeamMembership"/>); a foreign / session-less / missing run is an indistinguishable not-found
/// (null → 404).
/// </summary>
public sealed record GetSessionByRunQuery : IQuery<SessionDetail?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }
}
