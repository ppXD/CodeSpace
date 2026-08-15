namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Stable team-scoped routing identity for one versioned data class, such as <c>agent-run-log/v1</c>. A route is
/// control-plane policy only: every accepted write persists the exact storage profile revision into its artifact
/// location, so historical reads never depend on this mutable pointer.
/// </summary>
public class StorageRoute : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string DataClassTypeKey { get; set; } = string.Empty;
    public int CurrentRevision { get; set; } = 1;
    public StorageRouteState State { get; set; } = StorageRouteState.Draft;
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public uint Xmin { get; set; }

    public Team Team { get; set; } = default!;
    public ICollection<StorageRouteRevision> Revisions { get; set; } = new List<StorageRouteRevision>();
}

public enum StorageRouteState
{
    Draft,
    Active,
    Disabled,
    Retired,
}
