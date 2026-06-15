using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Launch.Providers.Chat;

/// <summary>
/// The <c>chat</c> launch seed provider (Rule 18.3 — one impl beside its variant folder): a free-text chat task
/// where the GOAL IS the task text. Self-registers via <see cref="ISingletonDependency"/>; a new surface is a
/// sibling provider folder, never an edit to the launch service / registry.
///
/// <para>Chat has NO source entity to derive a goal from, so it REQUIRES a non-blank
/// <see cref="TaskLaunchRequest.TaskText"/> — a blank goal throws a clear validation error rather than launching an
/// empty agent. It reads NOTHING from the surface payload (a chat launch carries no opaque context); the richer
/// pr / issue / project / repo providers (a later PR) are the ones that read <c>SurfacePayload</c> + attach a
/// <c>LinkedEntityRef</c>.</para>
/// </summary>
public sealed class ChatSeedProvider : ITaskLaunchSeedProvider, ISingletonDependency
{
    public string SurfaceKind => TaskLaunchSurfaceKinds.Chat;

    public Task<TaskLaunchSeed> SeedAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TaskText))
            throw new InvalidOperationException("A chat task requires a non-blank task text — there is no source entity to derive a goal from.");

        var seed = new TaskLaunchSeed
        {
            Goal = request.TaskText.Trim(),
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TeamId = request.TeamId,
            RepositoryId = request.RepositoryId,
            BaseBranch = request.BaseBranch,
        };

        return Task.FromResult(seed);
    }
}
