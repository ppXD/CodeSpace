namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Immutable team-scoped content identity. The digest is binary at rest; textual encodings belong at API edges.
/// A digest collision with a different size conflicts on the unique digest key instead of minting a second object.
/// </summary>
public class ArtifactObject : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public ArtifactDigestAlgorithm DigestAlgorithm { get; set; } = ArtifactDigestAlgorithm.Sha256;
    public byte[] Digest { get; set; } = [];
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }

    public Team Team { get; set; } = default!;
    public ICollection<ArtifactLocation> Locations { get; set; } = new List<ArtifactLocation>();
}

/// <summary>Algorithms accepted by the current CAS schema. Expansion is an explicit schema/version decision.</summary>
public enum ArtifactDigestAlgorithm
{
    Sha256,
}
