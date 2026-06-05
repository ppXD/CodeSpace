using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Chat;

/// <summary>
/// Post a message to a conversation. The caller must be an active member (the service gates on
/// it). <see cref="Body"/> is stored verbatim — its inline <c>&lt;reftype:refid|label&gt;</c>
/// tokens are parsed server-side into reference rows for reverse lookup, with zero hardcoded
/// reference types. Returns the rendered message including its parsed reference chips.
/// </summary>
public sealed record PostMessageCommand : ICommand<MessageView>, IRequireTeamMembership
{
    public Guid ConversationId { get; init; }
    public required string Body { get; init; }

    /// <summary>Optional thread parent. Must belong to the same conversation; the service rejects
    /// a reply that points at a message elsewhere.</summary>
    public Guid? ReplyToMessageId { get; init; }
}
