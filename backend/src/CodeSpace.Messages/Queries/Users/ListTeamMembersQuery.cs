using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Users;
using MediatR;

namespace CodeSpace.Messages.Queries.Users;

/// <summary>
/// Members of the caller's current team (from <c>X-Team-Id</c>), name-sorted. The pipeline
/// gates on team membership, so a caller only ever lists members of a team they belong to.
/// Used to resolve message author ids to names/avatars and to power the <c>@</c>-mention picker.
/// </summary>
public sealed record ListTeamMembersQuery : IRequest<IReadOnlyList<TeamMemberSummary>>, IRequireTeamMembership
{
}
