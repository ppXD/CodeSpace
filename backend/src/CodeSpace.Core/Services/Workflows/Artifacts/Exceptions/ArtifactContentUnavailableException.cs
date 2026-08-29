using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;

/// <summary>
/// A required artifact reference could not produce verified bytes. This is a storage-plane fact, not an empty
/// document and not a model/task failure; authoritative consumers must fail closed with this typed reason.
/// </summary>
public sealed class ArtifactContentUnavailableException : Exception, IFailure
{
    public ArtifactContentUnavailableException(Guid artifactId, ArtifactContentUnavailableKind kind, Exception? innerException = null)
        : base(MessageFor(artifactId, kind), innerException)
    {
        ArtifactId = artifactId;
        Kind = kind;
    }

    public Guid ArtifactId { get; }
    public ArtifactContentUnavailableKind Kind { get; }

    FailureKind IFailure.Kind => FailureKind.Unavailable;
    string IFailure.Code => FailureCodes.ArtifactContentUnavailable;
    string? IFailure.ClientMessage => "Required saved content is unavailable or could not be verified.";

    private static string MessageFor(Guid artifactId, ArtifactContentUnavailableKind kind) => kind switch
    {
        ArtifactContentUnavailableKind.MetadataMissing => $"Required artifact {artifactId} has no team-visible metadata; the saved work cannot be verified.",
        ArtifactContentUnavailableKind.PhysicalObjectMissing => $"Required artifact {artifactId} metadata exists, but its stored bytes are missing; restore the artifact backend or recover the work from its confirmed branch/PR.",
        ArtifactContentUnavailableKind.IntegrityFailure => $"Required artifact {artifactId} does not match what was recorded for it at its destination; refusing to use content that may not be the artifact.",
        ArtifactContentUnavailableKind.AccessDenied => $"Required artifact {artifactId} could not be read because the storage backend denied access.",
        _ => $"Required artifact {artifactId} is temporarily unavailable from the storage backend.",
    };
}

public enum ArtifactContentUnavailableKind
{
    MetadataMissing = 0,
    PhysicalObjectMissing = 1,
    IntegrityFailure = 2,
    BackendUnavailable = 3,
    AccessDenied = 4,
}
