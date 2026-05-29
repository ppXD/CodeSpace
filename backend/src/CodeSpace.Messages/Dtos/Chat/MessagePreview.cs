namespace CodeSpace.Messages.Dtos.Chat;

/// <summary>
/// A conversation's most-recent message, trimmed for a list row: a token-stripped, truncated
/// plain-text <see cref="Preview"/> (the list never ships whole 16k bodies) plus who sent it and
/// when. <see cref="IsDeleted"/> means the last message is a tombstone — the preview is blank and
/// the row renders "message deleted".
/// </summary>
public sealed record MessagePreview
{
    public required Guid MessageId { get; init; }
    public required Guid AuthorUserId { get; init; }
    public required string Preview { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required bool IsDeleted { get; init; }

    /// <summary>True when this last message <c>@</c>-mentions the user the list was loaded for — lets
    /// the row flag "you were mentioned here". Read from the reference reverse index, so it holds even
    /// when the mention sits past the preview's truncation. Always false on the single-get path.</summary>
    public bool MentionsViewer { get; init; }
}
