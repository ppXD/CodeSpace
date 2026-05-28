namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// A single chat message. Deliberately lean and high-volume-friendly: it does NOT
/// implement <see cref="IAuditable"/> (the generic created_by / last_modified_by columns
/// would duplicate <see cref="AuthorUserId"/> / <see cref="EditedDate"/> and cost write
/// bandwidth on the hottest table in the system).
///
/// <para><b>Id is UUID v7</b> — time-sortable. This is the performance backbone:
/// <list type="bullet">
///   <item>the <c>(conversation_id, id)</c> index yields chronological order for free —
///         no separate timestamp sort;</item>
///   <item>cursor pagination ("messages before / after id X") is a single index range
///         scan, O(log n) to any point in a conversation with billions of messages;</item>
///   <item>inserts have no per-conversation sequence contention (unlike a bigint seq).</item>
/// </list>
/// The service layer generates the id via <c>Guid.CreateVersion7()</c>; the DB just stores
/// a uuid. Read cursors (<see cref="ConversationMember.LastReadMessageId"/>) compare against
/// it directly because v7 ids sort by creation time.</para>
///
/// <para>Full-text search rides a generated <c>tsvector</c> column + GIN index defined in
/// migration 0028 — not a property here (it's a DB-computed column the app never writes).</para>
/// </summary>
public class Message : IEntity<Guid>
{
    /// <summary>UUID v7 (time-sortable). Generated in the service layer via Guid.CreateVersion7().</summary>
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>Denormalised team — avoids joining through conversation on every tenancy-filtered read.</summary>
    public Guid TeamId { get; set; }

    public Guid AuthorUserId { get; set; }

    /// <summary>
    /// Markdown source. Inline references (<c>@user</c>, <c>@pr</c>, …) are encoded as stable
    /// tokens in the text AND denormalised into <see cref="MessageReference"/> rows for
    /// reverse lookup. The body stays self-contained so a message renders without the
    /// reference table; the table exists for "find all messages mentioning X" queries.
    /// </summary>
    public string Body { get; set; } = default!;

    /// <summary>
    /// Thread parent. Null for top-level messages. Reserved for the threading feature —
    /// the column ships now so enabling threads later needs no migration, but MVP leaves
    /// it null and renders a flat timeline.
    /// </summary>
    public Guid? ReplyToMessageId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    /// <summary>Set on edit; null for never-edited messages. Drives the "(edited)" marker.</summary>
    public DateTimeOffset? EditedDate { get; set; }

    /// <summary>Soft delete — keeps thread continuity + audit; renders as "message deleted".</summary>
    public DateTimeOffset? DeletedDate { get; set; }

    public Conversation Conversation { get; set; } = default!;
}
