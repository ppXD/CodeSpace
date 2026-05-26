using CodeSpace.Messages.Dtos.Projects;

namespace CodeSpace.Core.Services.Projects;

/// <summary>
/// CRUD surface for the <c>project</c> table. A Project is purely a Variable namespace —
/// workflows reference its variables via the dotted path <c>project.{slug}.{var_name}</c>.
/// No FK ties workflows / repositories / runs to Projects, so this service has no
/// cross-entity orchestration: it just persists Project rows + serves slug lookups for the
/// variable resolver.
///
/// <para>Tenant boundary: every method takes <c>teamId</c> from <c>ICurrentTeam.Id</c>
/// (handler-resolved). Pipeline behaviour <c>TeamMembershipAuthorizationBehavior</c> proves
/// the caller belongs to that team before the handler runs.</para>
/// </summary>
public interface IProjectService
{
    /// <summary>List active projects in a team, ordered by created_date ascending.</summary>
    Task<IReadOnlyList<ProjectSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken);

    /// <summary>Single-row read by id. Returns null when no active row exists.</summary>
    Task<ProjectSummary?> GetAsync(Guid projectId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>Single-row read by slug. Used by the variable resolver. Returns null when absent.</summary>
    Task<ProjectSummary?> GetBySlugAsync(string slug, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Create a new project. Slug must be unique within the team (DB partial-unique index
    /// rejects duplicates with a 23505 — handler maps to a friendly error).
    /// </summary>
    Task<Guid> CreateAsync(Guid teamId, string slug, string name, string? description, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>Update name + description. Slug is immutable (changing it would invalidate workflow variable refs).</summary>
    Task UpdateAsync(Guid projectId, Guid teamId, string name, string? description, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-delete the project + its variables. Variables are cascade-soft-deleted because
    /// they reference the project via <c>scope_id</c>. Workflows referencing this project's
    /// variables will fail save-time validation on next edit (intentional — operator should
    /// see "project X is gone, fix your refs").
    /// </summary>
    Task DeleteAsync(Guid projectId, Guid teamId, Guid actorUserId, CancellationToken cancellationToken);
}
