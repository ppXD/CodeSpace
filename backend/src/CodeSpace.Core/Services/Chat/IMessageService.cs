using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Dtos.Chat.Interactions;

namespace CodeSpace.Core.Services.Chat;

/// <summary>
/// The message half of the chat foundation: post / list (keyset-paginated) / edit / delete and
/// the per-member read cursor. Reusable across every entry point (controller, future SignalR
/// hub, jobs) — no coupling to MediatR or ASP.NET. Every method is team-scoped and gates on
/// active membership before touching a conversation.
/// </summary>
public interface IMessageService
{
    Task<MessageView> PostAsync(Guid teamId, Guid authorUserId, Guid conversationId, string body, Guid? replyToMessageId, CancellationToken cancellationToken);

    /// <summary>
    /// Post a message carrying an interactive component (action buttons, …). Same membership / body
    /// rules + reference extraction as <see cref="PostAsync"/>; additionally persists the
    /// <paramref name="interaction"/> (its response target stays server-side). Used by the
    /// <c>chat.post_message</c> workflow node to drop an actionable card into a conversation.
    /// </summary>
    Task<MessageView> PostInteractiveAsync(Guid teamId, Guid authorUserId, Guid conversationId, string body, MessageInteraction interaction, CancellationToken cancellationToken);

    Task<MessagePage> ListAsync(Guid teamId, Guid userId, Guid conversationId, Guid? beforeId, int limit, CancellationToken cancellationToken);

    Task<MessageView> EditAsync(Guid teamId, Guid editorUserId, Guid messageId, string newBody, CancellationToken cancellationToken);

    Task DeleteAsync(Guid teamId, Guid actorUserId, Guid messageId, CancellationToken cancellationToken);

    Task MarkReadAsync(Guid teamId, Guid userId, Guid conversationId, Guid lastReadMessageId, CancellationToken cancellationToken);
}
