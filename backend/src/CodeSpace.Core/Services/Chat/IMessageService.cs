using CodeSpace.Messages.Dtos.Chat;

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

    Task<MessagePage> ListAsync(Guid teamId, Guid userId, Guid conversationId, Guid? beforeId, int limit, CancellationToken cancellationToken);

    Task<MessageView> EditAsync(Guid teamId, Guid editorUserId, Guid messageId, string newBody, CancellationToken cancellationToken);

    Task DeleteAsync(Guid teamId, Guid actorUserId, Guid messageId, CancellationToken cancellationToken);

    Task MarkReadAsync(Guid teamId, Guid userId, Guid conversationId, Guid lastReadMessageId, CancellationToken cancellationToken);
}
