using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Launch;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.RoutePreview;

/// <summary>
/// Default <see cref="ITaskRoutePreviewService"/> — the first three steps of <see cref="TaskLaunchService"/>'s
/// pipeline and NOTHING after them: resolve the seed provider by the open surface kind → seed → validate every
/// repository TEAM-SCOPED (the same <see cref="ILaunchRepositoryScopeGuard"/> the launch uses) → route. It then
/// stops. No session is opened, no run is staged, no row is written — the preview is a QUESTION, and asking it
/// must never be indistinguishable from launching.
///
/// <para>The request→router mapping is <see cref="TaskLaunchService.BuildRouteRequest"/> itself, not a copy: a
/// preview that assembled its own <c>EffortRouteRequest</c> could drift from the launch it claims to predict,
/// and the operator would be answering a confirm card about a route they were never going to get.</para>
/// </summary>
public sealed class TaskRoutePreviewService : ITaskRoutePreviewService, IScopedDependency
{
    private readonly ITaskLaunchSeedProviderRegistry _seedProviders;
    private readonly ILaunchRepositoryScopeGuard _repositoryScope;
    private readonly IEffortRouter _router;

    public TaskRoutePreviewService(ITaskLaunchSeedProviderRegistry seedProviders, ILaunchRepositoryScopeGuard repositoryScope, IEffortRouter router)
    {
        _seedProviders = seedProviders;
        _repositoryScope = repositoryScope;
        _router = router;
    }

    public async Task<TaskRoutePreviewResult> PreviewAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        var seed = await _seedProviders.Resolve(request.SurfaceKind).SeedAsync(request, cancellationToken).ConfigureAwait(false);

        await _repositoryScope.EnsureInTeamAsync(seed, request, cancellationToken).ConfigureAwait(false);

        var route = await _router.RouteAsync(TaskLaunchService.BuildRouteRequest(seed, request), cancellationToken).ConfigureAwait(false);

        return new TaskRoutePreviewResult { Route = route };
    }
}
