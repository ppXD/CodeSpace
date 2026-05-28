using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Chat;
using MediatR;

namespace CodeSpace.Messages.Queries.Chat;

/// <summary>
/// One newest-first page of a conversation's history. The caller must be a member (the service
/// gates on it). Keyset pagination: omit <see cref="BeforeId"/> for the latest page, then pass
/// the previous page's <c>NextBeforeId</c> to scroll up through older messages.
/// </summary>
public sealed record ListMessagesQuery : IRequest<MessagePage>, IRequireTeamMembership
{
    public Guid ConversationId { get; init; }

    /// <summary>Cursor — return messages older than this id. Null fetches the latest page.</summary>
    public Guid? BeforeId { get; init; }

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    /// <summary>Page size; the service clamps to [1, <see cref="MaxPageSize"/>].</summary>
    public int Limit { get; init; } = DefaultPageSize;
}
