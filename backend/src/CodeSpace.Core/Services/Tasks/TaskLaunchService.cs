using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Launch;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks;

/// <summary>
/// Default <see cref="ITaskLaunchService"/> — a flat named-method pipeline (Rule 4/5): resolve the seed provider by
/// the open surface kind → seed → validate the repo TEAM-SCOPED (fail-closed) → route → build the agent profile →
/// project + start the snapshot run → return the handle + route. Holds no per-surface logic: the ONLY surface
/// dispatch is <c>_seedProviders.Resolve(surfaceKind)</c>, and the core NEVER reads the surface payload (only the
/// resolved provider does), so a new surface plugs in by registering a provider with zero edit here (the generic
/// spine).
/// </summary>
public sealed class TaskLaunchService : ITaskLaunchService, IScopedDependency
{
    private readonly ITaskLaunchSeedProviderRegistry _seedProviders;
    private readonly IEffortRouter _router;
    private readonly ITaskRunSnapshotFactory _factory;
    private readonly CodeSpaceDbContext _db;

    public TaskLaunchService(ITaskLaunchSeedProviderRegistry seedProviders, IEffortRouter router, ITaskRunSnapshotFactory factory, CodeSpaceDbContext db)
    {
        _seedProviders = seedProviders;
        _router = router;
        _factory = factory;
        _db = db;
    }

    public async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        var seed = await _seedProviders.Resolve(request.SurfaceKind).SeedAsync(request, cancellationToken).ConfigureAwait(false);

        await EnsureRepositoryInTeamAsync(seed, request, cancellationToken).ConfigureAwait(false);

        var route = await _router.RouteAsync(BuildRouteRequest(seed, request), cancellationToken).ConfigureAwait(false);

        var profile = BuildAgentProfile(request, seed, route);

        var context = new TaskBuildContext { Seed = seed, Route = route, AgentProfile = profile, GroundingContext = seed.GroundingContext };

        var handle = await _factory.CreateAndRunAsync(context, request.TeamId, request.ActorUserId, cancellationToken).ConfigureAwait(false);

        return new LaunchTaskResult
        {
            RunId = handle.RunId,
            ProjectionKind = handle.ProjectionKind,
            Route = route,
            SurfaceKind = seed.SurfaceKind,
            LinkedEntity = seed.LinkedEntity,
        };
    }

    /// <summary>Validates the seed's (or request's) repo belongs to <c>request.TeamId</c>; a foreign / missing repo is a clear not-found — indistinguishable, so a foreign repo never leaks. Neither names a repo ⇒ skip (analysis-only is valid).</summary>
    private async Task EnsureRepositoryInTeamAsync(TaskLaunchSeed seed, TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        var repositoryId = seed.RepositoryId ?? request.RepositoryId;

        if (repositoryId == null) return;

        var inTeam = await _db.Repository.AsNoTracking()
            .AnyAsync(r => r.Id == repositoryId.Value && r.TeamId == request.TeamId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (!inTeam)
            throw new KeyNotFoundException($"Repository {repositoryId} not found or not accessible.");
    }

    /// <summary>Maps the seed + the operator's effort/recipe/autonomy onto the router input. PR4 leaves <c>CapsOverride</c> null (the caps/supervisor seam a later PR fills).</summary>
    private static EffortRouteRequest BuildRouteRequest(TaskLaunchSeed seed, TaskLaunchRequest request) => new()
    {
        Seed = seed,
        RequestedEffort = request.RequestedEffort,
        RequestedRecipe = request.RequestedRecipe,
    };

    /// <summary>Pure mapping: the request overrides + (seed repo ?? request repo) + the CLAMPED autonomy → the agent envelope the projection stamps. Every field optional, folding to agent.code's own defaults. Internal (not private) so the clamp choke point is unit-pinned directly (InternalsVisibleTo), not only through integration coverage.</summary>
    internal static ResolvedAgentProfile BuildAgentProfile(TaskLaunchRequest request, TaskLaunchSeed seed, RoutePlan route) => new()
    {
        RepositoryId = seed.RepositoryId ?? request.RepositoryId,
        Harness = request.Overrides.Harness,
        Model = request.Overrides.Model,
        AgentDefinitionId = request.Overrides.AgentDefinitionId,
        ModelCredentialId = request.Overrides.ModelCredentialId,
        ModelCredentialModelId = request.Overrides.ModelCredentialModelId,
        RunnerKind = request.Overrides.RunnerKind,
        AutonomyLevel = ClampAutonomy(request, route),
    };

    /// <summary>
    /// The SINGLE choke point that pins the run's autonomy: clamp the operator's requested tier down to the route's
    /// <see cref="RouteCaps.AutonomyCeiling"/>, and stamp the CLAMPED tier string. A blank / unrecognised request
    /// folds to the route's recipe/effort default (NOT Unleashed); a blank / unrecognised ceiling means "no ceiling"
    /// (the top tier, so the clamp is a no-op and the requested tier passes through). The clamped string is what
    /// flows through projection → the agent.code node config → <c>AgentAutonomyPolicy.Derive</c> → the sandbox
    /// runner, so a Quick/Standard route can never run Trusted/Unleashed however the caller asks.
    /// </summary>
    private static string ClampAutonomy(TaskLaunchRequest request, RoutePlan route)
    {
        var requested = AgentAutonomyPolicy.Parse(request.Autonomy, AgentAutonomyPolicy.Parse(route.RecommendedAutonomy, AgentAutonomyLevel.Standard));

        var ceiling = AgentAutonomyPolicy.Parse(route.Caps.AutonomyCeiling, AgentAutonomyLevel.Unleashed);

        return AgentAutonomyPolicy.Clamp(requested, ceiling).ToString();
    }
}
