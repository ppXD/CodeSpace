using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Launch;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.RoutePreview;

/// <summary>Resolve the seed and validate repository scope, then persist the routing decision without opening a session or staging a run.</summary>
public sealed class TaskRoutePreviewService : ITaskRoutePreviewService, IScopedDependency
{
    private readonly ITaskLaunchSeedProviderRegistry _seedProviders;
    private readonly ILaunchRepositoryScopeGuard _repositoryScope;
    private readonly ITaskRouteSnapshotService _snapshots;
    private readonly ITaskProjectionRegistry _projections;

    public TaskRoutePreviewService(ITaskLaunchSeedProviderRegistry seedProviders, ILaunchRepositoryScopeGuard repositoryScope, ITaskRouteSnapshotService snapshots, ITaskProjectionRegistry projections)
    {
        _seedProviders = seedProviders;
        _repositoryScope = repositoryScope;
        _snapshots = snapshots;
        _projections = projections;
    }

    public async Task<TaskRoutePreviewResult> PreviewAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        var seed = await _seedProviders.Resolve(request.SurfaceKind).SeedAsync(request, cancellationToken).ConfigureAwait(false);

        await _repositoryScope.EnsureInTeamAsync(seed, request, cancellationToken).ConfigureAwait(false);

        var preview = await _snapshots.CreateAsync(request, seed, cancellationToken).ConfigureAwait(false);
        return preview with { AcceptanceCompatibility = DescribeAcceptance(preview.Route, request.RepositoryId ?? seed.RepositoryId) };
    }

    private TaskAcceptanceCompatibility DescribeAcceptance(RoutePlan route, Guid? repositoryId)
    {
        var unknown = new TaskAcceptanceCompatibility { ProjectionKind = route.ProjectionKind, Detail = "The resolved route has not advertised an operator-command acceptance adapter; compatibility is unknown." };
        if (!_projections.TryResolve(route.ProjectionKind, out var builder) || builder.OperatorAcceptance.AcceptsCommand is null) return unknown;
        var adapter = builder.OperatorAcceptance;
        if (adapter.AcceptsCommand == false) return unknown with { State = TaskAcceptanceCompatibilityState.Incompatible, Detail = "The resolved route does not consume an operator-command acceptance floor. Its plan items use separate acceptance contracts." };
        if (adapter.GradingKind != BenchmarkGradingKind.TestsPass) return unknown with { Detail = "The resolved acceptance adapter does not advertise the proposed argv input format." };
        var requiresRepository = !AgentAcceptanceContract.GradesFromDeliverables(new SupervisorAcceptanceSpec { Kind = adapter.GradingKind, Command = [] });
        var compatible = !requiresRepository || repositoryId is not null;
        return new TaskAcceptanceCompatibility
        {
            ProjectionKind = route.ProjectionKind, GradingKind = adapter.GradingKind.ToString(), RequiresRepository = requiresRepository,
            State = compatible ? TaskAcceptanceCompatibilityState.Compatible : TaskAcceptanceCompatibilityState.Incompatible,
            Detail = compatible ? "The resolved route and workspace support this acceptance input. Runtime prerequisites and the check result still require independent execution." : "This acceptance adapter requires a repository workspace for an argv check. Keep the proposal for review or use content criteria; no repository-free argv adapter is available on this route.",
        };
    }
}
