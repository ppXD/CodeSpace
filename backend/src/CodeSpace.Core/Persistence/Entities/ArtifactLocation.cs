namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Current provider observation for one exact object stored under one exact immutable storage-profile revision.
/// Identity never changes; observations advance <see cref="Revision"/> and append an <see cref="ArtifactLocationEvent"/>.
/// </summary>
public class ArtifactLocation : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid ArtifactObjectId { get; set; }
    public Guid StorageProfileRevisionId { get; set; }

    /// <summary>Opaque provider-neutral locator returned to the later storage adapter.</summary>
    public string Locator { get; set; } = string.Empty;

    /// <summary>Object key relative to the namespace captured by the storage profile revision.</summary>
    public string ObjectKey { get; set; } = string.Empty;

    public string? ProviderObjectVersion { get; set; }
    public string? ProviderETag { get; set; }
    public string? ProviderChecksumAlgorithm { get; set; }
    public byte[]? ProviderChecksum { get; set; }
    public long? ObservedSizeBytes { get; set; }
    public string? ContentEncoding { get; set; }
    public string? EncryptionKeyVersion { get; set; }
    public ArtifactLocationState State { get; set; } = ArtifactLocationState.Pending;
    public long Revision { get; set; } = 1;
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public uint Xmin { get; set; }

    public ArtifactObject ArtifactObject { get; set; } = default!;
    public StorageProfileRevision StorageProfileRevision { get; set; } = default!;
    public ICollection<ArtifactLocationEvent> Events { get; set; } = new List<ArtifactLocationEvent>();
}

/// <summary>Provider-neutral observation state; only a verified Available location is readable.</summary>
public enum ArtifactLocationState
{
    Pending,
    Available,
    Missing,
    Corrupt,
    Deleting,
    Deleted,
    Failed,
}
