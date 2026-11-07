using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Tasks.Launch;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.RoutePreview;

/// <summary>Resolve the seed and validate repository, model-pool and persona-pool scope — the launch's own checks — then persist the routing decision without opening a session or staging a run.</summary>
public sealed class TaskRoutePreviewService : ITaskRoutePreviewService, IScopedDependency
{
    private readonly ITaskLaunchSeedProviderRegistry _seedProviders;
    private readonly ILaunchRepositoryScopeGuard _repositoryScope;
    private readonly ITaskRouteSnapshotService _snapshots;
    private readonly IModeProfileRegistry _modeProfiles;
    private readonly ILaunchControlResolver _controls;
    private readonly CodeSpaceDbContext _db;

    public TaskRoutePreviewService(ITaskLaunchSeedProviderRegistry seedProviders, ILaunchRepositoryScopeGuard repositoryScope, ITaskRouteSnapshotService snapshots, IModeProfileRegistry modeProfiles, ILaunchControlResolver controls, CodeSpaceDbContext db)
    {
        _seedProviders = seedProviders;
        _repositoryScope = repositoryScope;
        _snapshots = snapshots;
        _modeProfiles = modeProfiles;
        _controls = controls;
        _db = db;
    }

    public async Task<TaskRoutePreviewResult> PreviewAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        var seed = await _seedProviders.Resolve(request.SurfaceKind).SeedAsync(request, cancellationToken).ConfigureAwait(false);

        await _repositoryScope.EnsureInTeamAsync(seed, request, cancellationToken).ConfigureAwait(false);

        // The launch's own pool checks, in the launch's order: a pooled row disabled (or a persona deleted) after the
        // composer loaded is the same not-found here as at launch — never an Applied preview over a launch that 404s.
        await TaskLaunchService.EnsureModelsInTeamAsync(_db, request, cancellationToken).ConfigureAwait(false);

        await TaskLaunchService.EnsureAgentDefinitionsInTeamAsync(_db, request, cancellationToken).ConfigureAwait(false);

        var preview = await _snapshots.CreateAsync(request, seed, cancellationToken).ConfigureAwait(false);

        // The SAME resolution the launch refuses and clamps on — its acceptance verdict included — so a preview cannot
        // report a disposition, or a compatibility, the launch would not reach.
        var controls = await _controls.ResolveAsync(request, seed, preview.Route, cancellationToken).ConfigureAwait(false);

        return preview with
        {
            AcceptanceCompatibility = controls.AcceptanceCompatibility,
            Posture = DescribePosture(request, seed, preview.Route),
            ControlDispositions = controls.Dispositions,
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

        return new TaskRoutePosture
        {
            Autonomy = autonomy,
            NetworkOn = AgentAutonomyPolicy.Derive(effective).Network == AgentNetworkAccess.On,
            Network = network,
            Write = AgentAutonomyPolicy.DescribeWrite(effective),
            Approval = AgentAutonomyPolicy.DescribeApproval(effective),
            CompletionMode = completionMode,
            Completion = CompletionPolicy.Describe(completionMode),
        };
    }
}
