namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One append-only, INSTANCE-scope encrypted envelope for a <see cref="StorageDefault"/>. Deployment defaults carry
/// real credentials, and a template belongs to no team, so this is the team-less sibling of
/// <see cref="StorageCredentialRevision"/> — the same envelope convention, without the tenant columns.
/// <c>IPayloadEncryptor.Encrypt/Decrypt</c> take no team, so instance-scope ciphertext is representable with the
/// primitive already in the build.
///
/// <para>Rotation appends a new row and repoints <see cref="StorageDefault.CredentialId"/>; the superseded envelope
/// stays as history, its payload blanked once no template names it (0249). The materializer is the only reader: it
/// decrypts the envelope its template names and re-encrypts the secret into the team's own
/// <see cref="StorageCredential"/> before anything resolves through it.</para>
/// </summary>
public class StorageDefaultCredential : IEntity<Guid>
{
    public Guid Id { get; set; }

    /// <summary>Canonical major-versioned storage provider key, for example <c>aliyun-oss/v1</c>.</summary>
    public string ProviderTypeKey { get; set; } = string.Empty;

    /// <summary>
    /// Self-contained ciphertext produced by the established payload-encryption primitive. Never plaintext JSON or an
    /// individual key. Empty once no template names the envelope — the one change it ever admits (0249).
    /// </summary>
    public string EncryptedPayload { get; set; } = string.Empty;

    /// <summary>Optional pre-sanitized operator hint (for example a short masked tail); never consumed at runtime.</summary>
    public string? SafeHint { get; set; }

    /// <summary>SHA-256 of the encrypted envelope, allowing safe diagnostics without logging or indexing the envelope.</summary>
    public string EnvelopeFingerprint { get; set; } = string.Empty;

    /// <summary>Immutable append timestamp and actor. There are no last-modified fields: the only change admitted is blanking the payload.</summary>
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
}
