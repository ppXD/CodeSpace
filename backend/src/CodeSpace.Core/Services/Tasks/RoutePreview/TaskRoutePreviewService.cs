using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Launch;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.RoutePreview;

/// <summary>Resolve the seed and validate repository scope, then persist the routing decision without opening a session or staging a run.</summary>
public sealed class TaskRoutePreviewService : ITaskRoutePreviewService, IScopedDependency
{
    private readonly ITaskLaunchSeedProviderRegistry _seedProviders;
    private readonly ILaunchRepositoryScopeGuard _repositoryScope;
    private readonly ITaskRouteSnapshotService _snapshots;

    public TaskRoutePreviewService(ITaskLaunchSeedProviderRegistry seedProviders, ILaunchRepositoryScopeGuard repositoryScope, ITaskRouteSnapshotService snapshots)
    {
        _seedProviders = seedProviders;
        _repositoryScope = repositoryScope;
        _snapshots = snapshots;
    }

    public async Task<TaskRoutePreviewResult> PreviewAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        var seed = await _seedProviders.Resolve(request.SurfaceKind).SeedAsync(request, cancellationToken).ConfigureAwait(false);

        await _repositoryScope.EnsureInTeamAsync(seed, request, cancellationToken).ConfigureAwait(false);

        return await _snapshots.CreateAsync(request, seed, cancellationToken).ConfigureAwait(false);
    }
}
