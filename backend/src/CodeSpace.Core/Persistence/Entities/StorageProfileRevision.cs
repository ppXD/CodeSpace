namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One immutable configuration revision of a <see cref="StorageProfile"/>. Provider modules own config semantics;
/// this generic persistence boundary stores only an object-shaped NON-SECRET config document, an opaque reference to
/// credentials held elsewhere, and a one-way namespace fingerprint. There is deliberately no plaintext secret slot.
/// </summary>
public class StorageProfileRevision : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid StorageProfileId { get; set; }
    public int Revision { get; set; }

    /// <summary>Canonical major-versioned provider key, for example <c>aliyun-oss/v1</c>.</summary>
    public string ProviderTypeKey { get; set; } = string.Empty;

    /// <summary>Provider config validated against ConfigSchema. Never contains values from SecretSchema.</summary>
    public string NonSecretConfigJson { get; set; } = "{}";

    /// <summary>Opaque reference resolved by the future credential boundary. Never a key, token or secret value.</summary>
    public string? CredentialRef { get; set; }

    /// <summary>SHA-256 of the provider namespace identity (endpoint/account/container/bucket/prefix), never its plaintext.</summary>
    public string NamespaceFingerprint { get; set; } = string.Empty;

    /// <summary>Immutable append timestamp and actor. Revisions have no last-modified fields because mutation is illegal.</summary>
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }

    public StorageProfile Profile { get; set; } = default!;
}
