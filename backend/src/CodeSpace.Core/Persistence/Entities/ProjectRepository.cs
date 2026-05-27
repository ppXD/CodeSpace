namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Phase 3.1 N:M link between <see cref="Project"/> and <see cref="Repository"/>.
/// A repository may be linked to multiple projects (shared library across squads,
/// monorepo carving) and a project owns many repositories. Both sides expose
/// nav collections through this entity.
///
/// <para><b>Composite key</b>: (<see cref="ProjectId"/>, <see cref="RepositoryId"/>).
/// Soft-delete via <see cref="DeletedDate"/> preserves the audit trail across
/// detach/reattach cycles — a re-attached link reuses the same row (UPDATE
/// DeletedDate=NULL + bump LastModified*) so a future operator can grep the
/// row for "when was this repo first added to this project".</para>
///
/// <para><b>Tenancy</b>: <see cref="TeamId"/> is denormalised; service layer
/// MUST verify it matches both <c>Project.TeamId</c> and <c>Repository.TeamId</c>
/// at write time. A cross-team link is a tenancy leak by definition.</para>
/// </summary>
public class ProjectRepository : IAuditable
{
    public Guid ProjectId { get; set; }
    public Guid RepositoryId { get; set; }

    /// <summary>
    /// Denormalised team — same value as <c>Project.TeamId</c> and <c>Repository.TeamId</c>.
    /// Stored so tenancy-filtered queries can be indexed without joining through project.
    /// </summary>
    public Guid TeamId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }

    public Project Project { get; set; } = default!;
    public Repository Repository { get; set; } = default!;
}
