using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Chat;

/// <summary>
/// Operator-facing row for a conversation in the sidebar list. One shape covers DM / group /
/// channel — the frontend renders the title differently per <see cref="Kind"/> (a DM shows the
/// other member's name; a channel shows <c>#slug</c>).
///
/// <para>Message-derived fields (last-message preview, unread count) are intentionally absent
/// here — they arrive with the message layer (next PR). This DTO is the conversation-metadata
/// view only.</para>
/// </summary>
public sealed record ConversationSummary
{
    public required Guid Id { get; init; }
    public required ConversationKind Kind { get; init; }

    /// <summary>Channel handle (<c>#slug</c>); null for DM / group.</summary>
    public string? Slug { get; init; }

    /// <summary>Channel / group display name; null for DM (frontend renders the other member).</summary>
    public string? Name { get; init; }

    public string? Description { get; init; }
    public required ConversationVisibility Visibility { get; init; }
    public required bool Archived { get; init; }
    public required int MemberCount { get; init; }

    /// <summary>Member user ids — lets the frontend render a DM's other-party name + group
    /// avatars without a second round-trip. Bounded (DM=2, group/channel = team size).</summary>
    public required IReadOnlyList<Guid> MemberUserIds { get; init; }

    public required DateTimeOffset CreatedDate { get; init; }
}
