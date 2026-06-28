using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Sessions;

/// <summary>
/// The team's sessions index — every work thread the team owns, most-recently-active first, keyset-paginated for an
/// infinite-scroll sidebar. Team-scoped (<see cref="IRequireTeamMembership"/>); the team comes from <c>ICurrentTeam</c>,
/// never the wire. <see cref="Cursor"/> drives keyset pagination.
/// </summary>
public sealed record ListTeamSessionsQuery : IQuery<SessionPage>, IRequireTeamMembership
{
    /// <summary>Opaque keyset cursor from the previous page's <c>NextCursor</c>; null/absent = first page.</summary>
    public string? Cursor { get; init; }

    /// <summary>Page size; clamped to a sane range by the service.</summary>
    public int Limit { get; init; } = 30;
}
