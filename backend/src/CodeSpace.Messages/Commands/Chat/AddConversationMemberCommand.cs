using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Chat;

/// <summary>
/// Add a user to a channel / group. Idempotent (already-a-member is a no-op; a previously
/// removed member resurrects). Rejected for DMs (fixed pairs). <see cref="ConversationId"/>
/// is bound from the route by the controller (Rule 17).
/// </summary>
public sealed record AddConversationMemberCommand : ICommand<Unit>, IRequireTeamMembership
{
    public Guid ConversationId { get; init; }
    public required Guid UserId { get; init; }
}
