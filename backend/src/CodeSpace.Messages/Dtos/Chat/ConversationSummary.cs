using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Chat;

/// <summary>
/// Operator-facing row for a conversation in the chat list. One shape covers DM / group /
/// channel — the frontend renders the title differently per <see cref="Kind"/> (a DM shows the
/// other member's name; a channel shows <c>#slug</c>) and the row's preview from
/// <see cref="LastMessage"/>.
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

    /// <summary>Most-recent message preview, for the list row. Populated by the list ("recent
    /// conversations") path; null on a single get and for a conversation with no messages yet.</summary>
    public MessagePreview? LastMessage { get; init; }

    /// <summary>When the conversation last saw activity — the last message's time, else
    /// <see cref="CreatedDate"/>. The list sorts on this newest-first (the "recent" order).</summary>
    public required DateTimeOffset LastActivityDate { get; init; }

    /// <summary>The caller's read cursor — the id of the last message they've seen. Populated only
    /// by the single get (where "caller" is unambiguous); null in the list and until the caller has
    /// read anything. The frontend draws the unread divider above the first message past this id.</summary>
    public Guid? LastReadMessageId { get; init; }
}
