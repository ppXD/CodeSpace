namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// A single file's content for the Code browser's file viewer. Exactly one rendering state holds:
/// <see cref="Text"/> set (renderable UTF-8 text), <see cref="IsBinary"/> (not previewable — show a
/// placeholder), or <see cref="IsTruncated"/> (too large to inline — the provider never streams
/// megabytes into the SPA). <see cref="Size"/> is always the real byte size for display.
/// </summary>
public sealed record RemoteFileContent
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
    public bool IsBinary { get; init; }
    public bool IsTruncated { get; init; }
    public string? Text { get; init; }
    public string? Sha { get; init; }
}
