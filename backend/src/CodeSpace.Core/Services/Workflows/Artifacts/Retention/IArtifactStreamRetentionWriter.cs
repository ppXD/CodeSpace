using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Artifacts;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Retention;

/// <summary>
/// Sibling streaming face for a retention-declared write. It neither widens <see cref="IArtifactStore"/> nor changes
/// the fail-closed posture of ordinary <c>PutAsync</c> calls: only a producer whose complete holder set is enumerable
/// may opt into this contract.
/// </summary>
public interface IArtifactStreamRetentionWriter : IScopedDependency
{
    /// <summary>
    /// Store one re-readable source and mint its declaration in the same database transaction as the artifact metadata insert.
    /// A dedup hit never declares and revokes a live declaration already attached to the shared identity.
    /// </summary>
    Task<ArtifactStreamRetentionWrite> PutDeclaredAsync(ArtifactStreamRetentionWriteRequest request, CancellationToken cancellationToken);
}

public sealed record ArtifactStreamRetentionWriteRequest(ArtifactStreamWriteRequest Artifact, ArtifactRetentionClass RetentionClass, string HolderKind, Guid HolderId);

/// <summary>The admitted identity is returned so the holder never has to reopen its mutable source to derive metadata.</summary>
public sealed record ArtifactStreamRetentionWrite(Guid ArtifactId, bool Declared, string Sha256, long SizeBytes);
