using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Dtos.Chat.Interactions;

namespace CodeSpace.Core.Services.Chat;

/// <summary>
/// The bot half of chat: lets a workflow post into a conversation AS the team's "CodeSpace" bot —
/// a stable, attributable, non-human author. This is how a run with no human actor (e.g. a
/// PR-triggered review loop) drops a message / interactive card into team chat. Reusable across
/// every entry point (the chat.post_message node, future digest jobs); no MediatR / ASP.NET coupling.
/// </summary>
public interface IChatBotService
{
    /// <summary>
    /// The per-team CodeSpace bot user id, creating it on first use. Idempotent + race-safe (the
    /// bot's deterministic email rides the app_user unique-email index, so a concurrent create
    /// resolves to one winner). The bot is also made a team member on creation.
    /// </summary>
    Task<Guid> GetOrCreateTeamBotAsync(Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Post a message into <paramref name="conversationId"/> AS the team's bot — derives the team from
    /// the conversation, ensures the bot is a member (auto-join), then posts. When
    /// <paramref name="interaction"/> is non-null the message is an interactive card. The single entry
    /// point a workflow uses to talk to chat.
    /// </summary>
    Task<MessageView> PostAsBotAsync(Guid conversationId, string body, MessageInteraction? interaction, CancellationToken cancellationToken);
}
