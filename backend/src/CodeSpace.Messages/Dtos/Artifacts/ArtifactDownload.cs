namespace CodeSpace.Messages.Dtos.Artifacts;

/// <summary>The raw bytes + content type of one artifact, for download. Team-scoped at the query layer.</summary>
public sealed record ArtifactDownload
{
    public required Guid Id { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Bytes { get; init; }
}
