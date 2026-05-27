using CodeSpace.Messages.Dtos.Projects;

namespace CodeSpace.Core.Services.Projects;

/// <summary>
/// CRUD surface for project rows. Handlers are thin Mediator → service dispatchers (Rule 16);
/// all real business logic — slug validation, cascade-delete of project-scoped variables,
/// refuse-delete-when-repos-present — lives here.
///
/// <para>Tenant boundary: every method takes <c>teamId</c> from <c>ICurrentTeam</c> via the
/// handler. Service trusts the team id (MediatR pipeline already vetted membership) but
/// MUST scope every query by team_id so a stolen project-id can't read another team's row.</para>
/// </summary>
public interface IProjectService
{
    Task<IReadOnlyList<ProjectSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken);

    Task<ProjectSummary?> GetAsync(Guid teamId, Guid projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Create a new project. The slug is derived from <paramref name="name"/> by
    /// <c>ProjectService.SlugifyName</c> — operators never type identifiers directly.
    /// Throws when the derived slug collides with an existing active project; the
    /// caller surfaces a "rename to make it unique" prompt rather than auto-mangling.
    /// </summary>
    Task<Guid> CreateAsync(Guid teamId, string name, string? description, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomic-by-row "set membership" semantic for the legacy frontend Move-to-Project flow:
    /// soft-deletes every active <c>project_repository</c> link for the repo and attaches a
    /// fresh link to <paramref name="targetProjectId"/>. Throws when either the repo or the
    /// target project doesn't belong to this team. Idempotent — already-only-in-target is a
    /// no-op.
    ///
    /// <para>Phase 3.1 — Repository:Project is N:M. Callers that want to express true N:M
    /// membership (a repo in TWO projects) should use a future <c>AttachRepositoryAsync</c>
    /// / <c>DetachRepositoryAsync</c> pair instead of this Move primitive.</para>
    /// </summary>
    Task MoveRepositoryAsync(Guid teamId, Guid repositoryId, Guid targetProjectId, Guid actorUserId, CancellationToken cancellationToken);

    Task UpdateAsync(Guid teamId, Guid projectId, string name, string? description, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes the project + cascade soft-deletes its project-scoped variables.
    /// Throws <see cref="InvalidOperationException"/> when (a) the project still has active
    /// repositories or (b) the slug is "default" (every team must have one).
    /// </summary>
    Task DeleteAsync(Guid teamId, Guid projectId, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the team's "default" project id, creating it lazily if missing. Used by the
    /// binding flow as the fallback project to attach a new repository to via the
    /// <c>project_repository</c> link table when the operator hasn't picked one explicitly.
    /// Idempotent — concurrent callers all see the same row.
    /// </summary>
    Task<Guid> EnsureDefaultProjectAsync(Guid teamId, CancellationToken cancellationToken);
}
