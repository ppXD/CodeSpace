namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One immutable encrypted revision of a <see cref="StorageCredential"/>. A provider's arbitrary SecretSchema document
/// is serialized and encrypted as one opaque payload before crossing this boundary. ASP.NET Data Protection embeds the
/// key id in its protected envelope and resolves algorithms through the shared key-ring descriptor, so separate
/// key-version or algorithm columns would be a second, drift-prone source of truth.
/// </summary>
public class StorageCredentialRevision : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid StorageCredentialId { get; set; }
    public int Revision { get; set; }

    /// <summary>Canonical major-versioned storage provider key, for example <c>aliyun-oss/v1</c>.</summary>
    public string ProviderTypeKey { get; set; } = string.Empty;

    /// <summary>
    /// Self-contained ciphertext produced by the established payload-encryption primitive. It is never plaintext JSON,
    /// an individual key/token, or a model-credential reference.
    /// </summary>
    public string EncryptedPayload { get; set; } = string.Empty;

    /// <summary>Optional pre-sanitized operator hint (for example a short masked tail); never consumed at runtime.</summary>
    public string? SafeHint { get; set; }

    /// <summary>SHA-256 of the encrypted envelope, allowing safe diagnostics without logging or indexing the envelope.</summary>
    public string EnvelopeFingerprint { get; set; } = string.Empty;

    /// <summary>Immutable append timestamp and actor. Revisions have no last-modified fields because mutation is illegal.</summary>
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }

    public StorageCredential Credential { get; set; } = default!;
}
