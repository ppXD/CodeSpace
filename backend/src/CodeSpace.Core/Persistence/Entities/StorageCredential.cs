namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Provider-neutral, team-scoped identity for one operator-managed storage credential. The identity remains stable;
/// secret rotation appends a <see cref="StorageCredentialRevision"/> and advances <see cref="CurrentRevision"/>.
/// Revocation is terminal history, not a hard delete. A future trusted broker can project <see cref="Id"/> as an opaque
/// secret id and a revision ordinal as its opaque version; ciphertext never needs to enter a storage-driver snapshot or
/// credential handle. No runtime path consumes this additive ledger yet.
/// </summary>
public class StorageCredential : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }

    /// <summary>Stable lowercase operator key, unique inside a team. Display copy belongs to a later API/UI slice.</summary>
    public string StableName { get; set; } = string.Empty;

    /// <summary>
    /// Monotonic pointer to the current append-only revision. A deferred database FK proves that the pointed revision
    /// belongs to this exact team and credential while allowing identity and revision one to be committed together.
    /// </summary>
    public int CurrentRevision { get; set; } = 1;

    public StorageCredentialState State { get; set; } = StorageCredentialState.Active;

    /// <summary>Stable creation provenance. A revision has its own append actor and timestamp.</summary>
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }

    /// <summary>Both fields are null while active and both are populated exactly once on terminal revocation.</summary>
    public DateTimeOffset? RevokedDate { get; set; }
    public Guid? RevokedBy { get; set; }

    /// <summary>Npgsql xmin optimistic-concurrency token for current-revision and revocation transitions.</summary>
    public uint Xmin { get; set; }

    public Team Team { get; set; } = default!;
    public ICollection<StorageCredentialRevision> Revisions { get; set; } = new List<StorageCredentialRevision>();
}

/// <summary>Lifecycle of a stable storage credential identity. Revoked cannot be reactivated or revised.</summary>
public enum StorageCredentialState
{
    Active,
    Revoked,
}
