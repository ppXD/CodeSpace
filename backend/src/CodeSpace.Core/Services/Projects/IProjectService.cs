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

    Task<Guid> CreateAsync(Guid teamId, string slug, string name, string? description, Guid actorUserId, CancellationToken cancellationToken);

    Task UpdateAsync(Guid teamId, Guid projectId, string name, string? description, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes the project + cascade soft-deletes its project-scoped variables.
    /// Throws <see cref="InvalidOperationException"/> when (a) the project still has active
    /// repositories or (b) the slug is "default" (every team must have one).
    /// </summary>
    Task DeleteAsync(Guid teamId, Guid projectId, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the team's "default" project id, creating it lazily if missing. Used by the
    /// binding flow to fill <see cref="Persistence.Entities.Repository.ProjectId"/> when the
    /// operator hasn't picked a project. Idempotent — concurrent callers all see the same row.
    /// </summary>
    Task<Guid> EnsureDefaultProjectAsync(Guid teamId, CancellationToken cancellationToken);
}
