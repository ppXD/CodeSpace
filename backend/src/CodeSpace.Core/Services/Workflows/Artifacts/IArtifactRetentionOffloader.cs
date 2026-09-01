using CodeSpace.Messages.Artifacts;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// The holder-aware sibling of <see cref="IArtifactOffloader"/>. Only a producer whose artifact reference is fully
/// enumerable by the reference oracle may use this seam; ordinary callers remain fail-closed on permanent retention.
/// </summary>
public interface IArtifactRetentionOffloader
{
    /// <summary>
    /// Apply the ordinary UTF-8 inline threshold. The production scoped implementation stores an oversize value
    /// through an isolated declaring write; a legacy/custom offloader without that capability must keep the original
    /// plain-write behavior, whose permanent retention is the fail-closed fallback. The caller must mint
    /// <see cref="ArtifactRetentionOffloadRequest.HolderId"/> before calling and use that identity for its holder row.
    /// </summary>
    Task<OffloadedText> OffloadDeclaredIfLargeAsync(ArtifactRetentionOffloadRequest request, CancellationToken cancellationToken);
}

/// <summary>The complete intent for one holder-aware text offload.</summary>
public sealed record ArtifactRetentionOffloadRequest(Guid TeamId, string? Text, string ContentType, ArtifactRetentionClass RetentionClass, string HolderKind, Guid HolderId);
