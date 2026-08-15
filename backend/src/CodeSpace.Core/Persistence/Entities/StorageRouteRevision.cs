namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Immutable routing policy revision. <see cref="StorageProfileRevisionMode.CurrentAtWrite"/> resolves and stamps the
/// target profile's current revision while authorizing a write; <see cref="StorageProfileRevisionMode.Pinned"/> keeps
/// the route on one exact immutable profile revision. Neither mode affects already-persisted artifact locations.
/// </summary>
public class StorageRouteRevision : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid StorageRouteId { get; set; }
    public int Revision { get; set; }
    public Guid StorageProfileId { get; set; }
    public StorageProfileRevisionMode ProfileRevisionMode { get; set; }
    public int? PinnedProfileRevision { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }

    public StorageRoute Route { get; set; } = default!;
    public StorageProfile Profile { get; set; } = default!;
}

public enum StorageProfileRevisionMode
{
    CurrentAtWrite,
    Pinned,
}
