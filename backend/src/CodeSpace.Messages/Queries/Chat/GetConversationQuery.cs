using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Chat;
using MediatR;

namespace CodeSpace.Messages.Queries.Chat;

/// <summary>
/// A single conversation's metadata — only if the caller is an active member. Returns null
/// otherwise (the controller maps to 404), never leaking the existence of a conversation the
/// caller isn't in. <see cref="ConversationId"/> is bound from the route (Rule 17).
/// </summary>
public sealed record GetConversationQuery : IRequest<ConversationSummary?>, IRequireTeamMembership
{
    public required Guid ConversationId { get; init; }
}
