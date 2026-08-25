namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One append-only, INSTANCE-scope encrypted envelope for a <see cref="StorageDefault"/>. Deployment defaults carry
/// real credentials, and a template belongs to no team, so this is the team-less sibling of
/// <see cref="StorageCredentialRevision"/> — the same envelope convention, without the tenant columns.
/// <c>IPayloadEncryptor.Encrypt/Decrypt</c> take no team, so instance-scope ciphertext is representable with the
/// primitive already in the build.
///
/// <para>Rotation appends a new row and repoints <see cref="StorageDefault.CredentialId"/>; the superseded envelope
/// stays as history. No runtime path decrypts this yet — the materializer lane is the intended reader, and it will
/// re-encrypt the secret into the team's own <see cref="StorageCredential"/> before anything resolves through it.</para>
/// </summary>
public class StorageDefaultCredential : IEntity<Guid>
{
    public Guid Id { get; set; }

    /// <summary>Canonical major-versioned storage provider key, for example <c>aliyun-oss/v1</c>.</summary>
    public string ProviderTypeKey { get; set; } = string.Empty;

    /// <summary>Self-contained ciphertext produced by the established payload-encryption primitive. Never plaintext JSON or an individual key.</summary>
    public string EncryptedPayload { get; set; } = string.Empty;

    /// <summary>Optional pre-sanitized operator hint (for example a short masked tail); never consumed at runtime.</summary>
    public string? SafeHint { get; set; }

    /// <summary>SHA-256 of the encrypted envelope, allowing safe diagnostics without logging or indexing the envelope.</summary>
    public string EnvelopeFingerprint { get; set; } = string.Empty;

    /// <summary>Immutable append timestamp and actor. There are no last-modified fields because mutation is illegal.</summary>
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
}
