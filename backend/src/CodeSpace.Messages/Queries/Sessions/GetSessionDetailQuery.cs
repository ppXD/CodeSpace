using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Sessions;

/// <summary>
/// One thread as a conversation — its turns (each turn = a run; reruns nested as attempts). Team-scoped
/// (<see cref="IRequireTeamMembership"/>); a foreign / missing session is an indistinguishable not-found (null → 404).
/// </summary>
public sealed record GetSessionDetailQuery : IQuery<SessionDetail?>, IRequireTeamMembership
{
    public Guid SessionId { get; init; }
}
