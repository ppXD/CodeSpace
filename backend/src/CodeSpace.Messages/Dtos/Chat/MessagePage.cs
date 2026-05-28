namespace CodeSpace.Messages.Dtos.Chat;

/// <summary>
/// One page of a conversation's history, <b>newest-first</b> (matching the
/// <c>(conversation_id, id DESC)</c> index). The frontend reverses for display and scrolls
/// <i>up</i> through history by passing <see cref="NextBeforeId"/> back as the next request's
/// cursor.
///
/// <para>Cursor pagination — not offset — so paging stays O(log n) to any depth and never
/// skips/duplicates rows when new messages arrive mid-scroll. <see cref="NextBeforeId"/> is the
/// id of the oldest message in this page; null when the conversation has no older messages.</para>
/// </summary>
public sealed record MessagePage
{
    public required IReadOnlyList<MessageView> Messages { get; init; }

    /// <summary>Pass as <c>beforeId</c> to fetch the next older page. Null when at the start.</summary>
    public Guid? NextBeforeId { get; init; }

    public required bool HasMore { get; init; }
}
