namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>Authoritative bounded-memory verification of a committed artifact identity. No stream or receipt escapes before a fresh complete read, SHA-256/length match and observed EOF. Uncommitted metadata is unavailable. This completed read is not an atomic metadata/storage snapshot, retention lease or power-loss guarantee.</summary>
public interface IArtifactContentVerifier
{
    Task<ArtifactVerifiedContent> VerifyAsync(ArtifactContentVerificationRequest request, CancellationToken cancellationToken);
}

public sealed record ArtifactContentVerificationRequest(Guid TeamId, Guid ArtifactId, string Sha256, long SizeBytes);
public sealed record ArtifactVerifiedContent(Guid TeamId, Guid ArtifactId, string Sha256, long SizeBytes);

/// <summary>Optional streaming capability for a legacy backend. The caller owns the fresh readable stream. Missing capability fails closed; whole-byte and UI range reads are not substitutes.</summary>
public interface IArtifactBlobStreamReader
{
    Task<Stream> OpenReadAsync(string storageUrl, CancellationToken cancellationToken);
}
