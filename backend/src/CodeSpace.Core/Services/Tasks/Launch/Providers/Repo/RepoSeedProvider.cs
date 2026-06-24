using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Launch.Providers.Repo;

/// <summary>
/// The <c>repo</c> launch seed provider (Rule 18.3 — one impl beside its variant folder): a task launched against
/// a WHOLE repository, where the operator's task text IS the goal and the selected repository is the scope it runs
/// over. Self-registers via <see cref="ISingletonDependency"/>; a new surface is a sibling provider folder, never
/// an edit to the launch service / registry.
///
/// <para>A repository task has TWO surface-required inputs — there is no source entity to derive a goal from
/// (unlike a pr / issue), so it needs a non-blank <see cref="TaskLaunchRequest.TaskText"/> (WHAT to do), and a
/// <see cref="TaskLaunchRequest.RepositoryId"/> (the repository to do it against). Either missing throws a clear
/// validation error rather than launching an unscoped / empty agent. The repo's tenancy is validated TEAM-SCOPED
/// by the launch service; the provider only normalizes. It reads NOTHING from the surface payload — the repo IS
/// the scope, carried as the seed's <see cref="TaskLaunchSeed.RepositoryId"/>.</para>
/// </summary>
public sealed class RepoSeedProvider : ITaskLaunchSeedProvider, ISingletonDependency
{
    public string SurfaceKind => TaskLaunchSurfaceKinds.Repo;

    public Task<TaskLaunchSeed> SeedAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TaskText))
            throw new InvalidOperationException("A repository task requires a non-blank task text — it names the work to do against the repository.");

        if (request.RepositoryId is null)
            throw new InvalidOperationException("A repository task requires a target repository — there is nothing to scope the work to without one.");

        var seed = new TaskLaunchSeed
        {
            Goal = request.TaskText.Trim(),
            SurfaceKind = TaskLaunchSurfaceKinds.Repo,
            TeamId = request.TeamId,
            RepositoryId = request.RepositoryId,
            BaseBranch = request.BaseBranch,
        };

        return Task.FromResult(seed);
    }
}
