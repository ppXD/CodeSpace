namespace CodeSpace.Messages.Dtos.Chat;

/// <summary>
/// A rendered chat message. <see cref="Body"/> keeps the markdown + inline reference tokens so
/// it renders standalone; <see cref="References"/> is the parsed-out chip data the frontend uses
/// to draw <c>@</c>-pills and deep links without re-tokenising the body itself.
///
/// <para>A soft-deleted message still appears (so thread continuity holds) as a tombstone:
/// <see cref="IsDeleted"/> is true, <see cref="Body"/> is blanked, and <see cref="References"/>
/// is empty — the original content never leaves the server once deleted.</para>
/// </summary>
public sealed record MessageView
{
    public required Guid Id { get; init; }
    public required Guid ConversationId { get; init; }
    public required Guid AuthorUserId { get; init; }
    public required string Body { get; init; }

    /// <summary>Thread parent; null for a top-level message. Carried now for the threading
    /// feature — the MVP renders a flat timeline and leaves this null.</summary>
    public Guid? ReplyToMessageId { get; init; }

    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>Set when the author has edited; drives the "(edited)" marker. Null otherwise.</summary>
    public DateTimeOffset? EditedDate { get; init; }

    public required bool IsDeleted { get; init; }

    public required IReadOnlyList<MessageReferenceView> References { get; init; }
}
