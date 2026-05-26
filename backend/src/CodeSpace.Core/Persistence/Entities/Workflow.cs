namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The design-time artifact a user edits — name, description, current definition JSON,
/// enabled flag. Every save bumps <see cref="LatestVersion"/> and copies the prior JSON
/// into a <see cref="WorkflowVersion"/> row so already-running runs stay pinned to the
/// definition they started with.
/// </summary>
public class Workflow : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }

    public string Name { get; set; } = default!;
    public string? Description { get; set; }

    /// <summary>Live definition. Serialized JSON of <c>WorkflowDefinition</c>.</summary>
    public string DefinitionJson { get; set; } = default!;
    public int LatestVersion { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }

    public Team Team { get; set; } = default!;
}
