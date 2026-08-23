using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;

/// <summary>
/// The selected legacy byte backend cannot consume a stream. Streaming writes fail explicitly instead of falling
/// back to materializing the full payload behind the caller's back.
/// </summary>
public sealed class ArtifactStreamingWriteUnavailableException : NotSupportedException, IFailure
{
    public ArtifactStreamingWriteUnavailableException(Type backendType)
        : this(backendType, typeof(IArtifactBlobStreamWriter)) { }

    public ArtifactStreamingWriteUnavailableException(Type componentType, Type requiredCapability)
        : base($"Artifact component '{componentType.FullName}' does not implement required streaming write capability '{requiredCapability.FullName}'; refusing a whole-payload fallback.")
    {
        ComponentType = componentType;
        RequiredCapability = requiredCapability;
    }

    public Type ComponentType { get; }
    public Type RequiredCapability { get; }

    /// <summary>Compatibility alias for the original legacy-backend caller.</summary>
    public Type BackendType => ComponentType;

    FailureKind IFailure.Kind => FailureKind.Internal;
    string IFailure.Code => FailureCodes.Internal;
}
