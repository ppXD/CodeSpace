using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Visibility;
using MediatR;

namespace CodeSpace.Messages.Queries.Users;

/// <summary>
/// Team member identities for DISPLAY — like <see cref="ListTeamMembersQuery"/> but INCLUDES the
/// team's CodeSpace bot, so the chat UI can resolve a message author's name/avatar when the author
/// is the bot. Marked <see cref="IBotInclusive"/> so bots are visible for this request only; the
/// plain member list + the <c>@</c>-mention picker (<see cref="ListTeamMembersQuery"/>) stay bot-free.
/// </summary>
public sealed record ListTeamMemberIdentitiesQuery : IRequest<IReadOnlyList<TeamMemberSummary>>, IRequireTeamMembership, IBotInclusive
{
}
