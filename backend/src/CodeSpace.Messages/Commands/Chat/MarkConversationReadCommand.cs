using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Chat;

/// <summary>
/// Advance the caller's read cursor in a conversation to <see cref="LastReadMessageId"/>. Unread
/// counts derive from this single per-member cursor (messages with a later id), so there is no
/// per-message read-receipt fan-out. The cursor only ever moves forward — a stale client can't
/// drag it backward and resurrect "unread" on already-seen messages.
/// </summary>
public sealed record MarkConversationReadCommand : ICommand<Unit>, IRequireTeamMembership
{
    public Guid ConversationId { get; init; }
    public required Guid LastReadMessageId { get; init; }
}
