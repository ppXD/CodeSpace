namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Provider-neutral, team-scoped identity for one operator-managed storage destination. The identity and
/// <see cref="StableName"/> remain stable; configuration changes append a <see cref="StorageProfileRevision"/> and
/// advance <see cref="CurrentRevision"/>. This additive ledger is intentionally not consumed by ArtifactStore yet.
/// </summary>
public class StorageProfile : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }

    /// <summary>Stable lowercase operator key, unique inside a team. Display copy belongs to the later API/UI slice.</summary>
    public string StableName { get; set; } = string.Empty;

    /// <summary>
    /// Monotonic pointer to the current append-only revision. A database-level deferred composite FK proves that the
    /// pointed revision belongs to this exact team/profile while still allowing both rows to be created atomically.
    /// </summary>
    public int CurrentRevision { get; set; } = 1;

    public StorageProfileState State { get; set; } = StorageProfileState.Draft;

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Npgsql xmin optimistic-concurrency token for current-revision and state transitions.</summary>
    public uint Xmin { get; set; }

    public Team Team { get; set; } = default!;
    public ICollection<StorageProfileRevision> Revisions { get; set; } = new List<StorageProfileRevision>();
}

/// <summary>Lifecycle of a stable storage profile identity. Retired is terminal history, not a hard delete.</summary>
public enum StorageProfileState
{
    Draft,
    Active,
    Disabled,
    Retired,
}
