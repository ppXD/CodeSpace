using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Completion;
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
    private readonly IModeProfileRegistry _modeProfiles;

    public TaskRoutePreviewService(ITaskLaunchSeedProviderRegistry seedProviders, ILaunchRepositoryScopeGuard repositoryScope, ITaskRouteSnapshotService snapshots, ITaskProjectionRegistry projections, IModeProfileRegistry modeProfiles)
    {
        _seedProviders = seedProviders;
        _repositoryScope = repositoryScope;
        _snapshots = snapshots;
        _projections = projections;
        _modeProfiles = modeProfiles;
    }

    public async Task<TaskRoutePreviewResult> PreviewAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        var seed = await _seedProviders.Resolve(request.SurfaceKind).SeedAsync(request, cancellationToken).ConfigureAwait(false);

        await _repositoryScope.EnsureInTeamAsync(seed, request, cancellationToken).ConfigureAwait(false);

        var preview = await _snapshots.CreateAsync(request, seed, cancellationToken).ConfigureAwait(false);

        return preview with
        {
            AcceptanceCompatibility = DescribeAcceptance(preview.Route, request.RepositoryId ?? seed.RepositoryId),
            Posture = DescribePosture(request, seed, preview.Route),
        };
    }

    /// <summary>
    /// The posture the LAUNCH will actually take for this exact request — computed by calling the SAME production
    /// derivations <see cref="TaskLaunchService.LaunchAsync"/> calls, so this can never state a posture the launch
    /// would not also reach. Autonomy: <see cref="TaskLaunchService.BuildAgentProfile"/>'s own clamp (the identical
    /// choke point that stamps the projected run). Network: <see cref="AgentAutonomyPolicy.DescribeNetwork"/> fed by
    /// that SAME clamped tier — no run exists yet, so no confinement record (its own pre-launch caveat wording).
    /// Completion: <see cref="CompletionPolicy.StampModeFor"/> fed by the SAME <see cref="RunModeClassifier"/>
    /// reading a task launch's run-staging uses — a task route always carries a <c>ProjectionKind</c>, so the
    /// classifier reads it directly and never needs to see a definition json (mirrors <c>RunFromSnapshotStarter</c>).
    /// </summary>
    private TaskRoutePosture DescribePosture(TaskLaunchRequest request, TaskLaunchSeed seed, RoutePlan route)
    {
        var autonomy = TaskLaunchService.BuildAgentProfile(request, seed, route).AutonomyLevel!;
        var effective = AgentAutonomyPolicy.Parse(autonomy, AgentAutonomyLevel.Standard);
        var ceiling = AgentAutonomyPolicy.Parse(route.Caps.AutonomyCeiling, AgentAutonomyPolicy.UnboundedRouteCeiling);
        var network = AgentAutonomyPolicy.DescribeNetwork(effective, ceiling, AgentAutonomyPolicy.DeploymentCeiling);

        var mode = RunModeClassifier.DeriveFromJson(route.ProjectionKind, definitionJson: null);
        var completionMode = CompletionPolicy.StampModeFor(request.CompletionMode, mode, _modeProfiles.Resolve(mode));

        return new TaskRoutePosture { Autonomy = autonomy, NetworkOn = AgentAutonomyPolicy.Derive(effective).Network == AgentNetworkAccess.On, Network = network, CompletionMode = completionMode };
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
