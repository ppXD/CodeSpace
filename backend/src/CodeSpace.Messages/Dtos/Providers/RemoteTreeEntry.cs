using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// One entry at a single level of a repository tree — what the Code browser lists when a folder
/// is opened. <see cref="Path"/> is the full repo-root-relative path used to drill into folders
/// or fetch a file's content. <see cref="Size"/> is null for directories (and for providers that
/// don't report it on the tree listing).
/// </summary>
public sealed record RemoteTreeEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required RemoteTreeEntryType Type { get; init; }
    public long? Size { get; init; }
    public string? Sha { get; init; }
}
