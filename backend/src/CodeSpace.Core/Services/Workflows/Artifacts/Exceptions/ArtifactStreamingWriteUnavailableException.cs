namespace CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;

/// <summary>
/// The selected legacy byte backend cannot consume a stream. Streaming writes fail explicitly instead of falling
/// back to materializing the full payload behind the caller's back.
/// </summary>
public sealed class ArtifactStreamingWriteUnavailableException : NotSupportedException
{
    public ArtifactStreamingWriteUnavailableException(Type backendType)
        : base($"Artifact blob backend '{backendType.FullName}' does not implement the required streaming write capability; refusing a whole-payload fallback.") => BackendType = backendType;

    public Type BackendType { get; }
}
