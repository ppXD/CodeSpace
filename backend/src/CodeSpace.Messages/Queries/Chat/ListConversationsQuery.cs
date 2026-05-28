using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Chat;
using MediatR;

namespace CodeSpace.Messages.Queries.Chat;

/// <summary>
/// Every conversation the caller is an active member of, in the caller's team, newest-first.
/// Drives the chat sidebar. Membership scoping happens in the service — a user only ever sees
/// conversations they belong to (public channels they haven't joined are a separate directory
/// query, added later).
/// </summary>
public sealed record ListConversationsQuery : IRequest<IReadOnlyList<ConversationSummary>>, IRequireTeamMembership
{
}
