using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Users;

/// <summary>
/// Returns the authenticated caller's profile plus the teams they belong to (Owner +
/// TeamMembership rows). Used by the SPA on first load to populate the workspace switcher.
/// </summary>
public sealed record MeQuery : IQuery<MeResponse>, IRequireAuthenticatedUser
{
}
