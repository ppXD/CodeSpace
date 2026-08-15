namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Append-only observation history for an <see cref="ArtifactLocation"/>. Provider-specific, non-secret diagnostic
/// facts may live in <see cref="DetailsJson"/>; the typed columns remain the cross-provider query contract.
/// </summary>
public class ArtifactLocationEvent : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid ArtifactLocationId { get; set; }
    public long Revision { get; set; }
    public ArtifactLocationEventType EventType { get; set; }
    public ArtifactLocationState State { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public string? ProviderObjectVersion { get; set; }
    public string? ProviderETag { get; set; }
    public string? ProviderChecksumAlgorithm { get; set; }
    public byte[]? ProviderChecksum { get; set; }
    public long? ObservedSizeBytes { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? ContentEncoding { get; set; }
    public string? EncryptionKeyVersion { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public Guid CreatedBy { get; set; }

    public ArtifactLocation ArtifactLocation { get; set; } = default!;
}

public enum ArtifactLocationEventType
{
    Created,
    Observed,
    Verified,
    StateChanged,
    Failed,
}
