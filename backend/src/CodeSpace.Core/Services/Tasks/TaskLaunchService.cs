using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Launch;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Enums;
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
    private readonly IWorkSessionService _sessions;
    private readonly ISessionContextBuilder _sessionContext;
    private readonly ISessionBranchResolver _sessionBranches;
    private readonly CodeSpaceDbContext _db;

    public TaskLaunchService(ITaskLaunchSeedProviderRegistry seedProviders, IEffortRouter router, ITaskRunSnapshotFactory factory, IWorkSessionService sessions, ISessionContextBuilder sessionContext, ISessionBranchResolver sessionBranches, CodeSpaceDbContext db)
    {
        _seedProviders = seedProviders;
        _router = router;
        _factory = factory;
        _sessions = sessions;
        _sessionContext = sessionContext;
        _sessionBranches = sessionBranches;
        _db = db;
    }

    public async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        var seed = await _seedProviders.Resolve(request.SurfaceKind).SeedAsync(request, cancellationToken).ConfigureAwait(false);

        await EnsureRepositoryInTeamAsync(seed, request, cancellationToken).ConfigureAwait(false);

        var route = await _router.RouteAsync(BuildRouteRequest(seed, request), cancellationToken).ConfigureAwait(false);

        var profile = BuildAgentProfile(request, seed, route);

        // Resolve the thread this run is a turn of: CONTINUE the named session (the run becomes its next top-level
        // turn) or OPEN a new one. Both stage onto the same unit of work as the run, so they commit atomically — a
        // launch that fails downstream (a rejected repo, an invalid continue target) leaves no orphan session.
        var session = request.ContinueSessionId is { } continueId
            ? await _sessions.ContinueAsync(continueId, request.TeamId, cancellationToken).ConfigureAwait(false)
            : await _sessions.OpenAsync(request.TeamId, seed.Goal, WorkSessionKind.Task, request.ActorUserId, cancellationToken).ConfigureAwait(false);

        // On a CONTINUE, prime the run with the thread's prior-turn digest — the projection folds this grounding into
        // the agent's prompt so the follow-up builds on earlier work. A fresh launch carries only the seed's own grounding.
        var grounding = await ResolveGroundingAsync(request, seed, cancellationToken).ConfigureAwait(false);

        // …and clone the primary repo at the prior turn's produced branch, so the follow-up builds on earlier CODE
        // (not just the narrative). Null on a fresh launch / no repo / no prior branch ⇒ the repo's default branch.
        var primaryBaseRef = await ResolveBaseRefAsync(request, seed, profile, cancellationToken).ConfigureAwait(false);

        var context = new TaskBuildContext { Seed = seed, Route = route, AgentProfile = profile, GroundingContext = grounding, PrimaryBaseRef = primaryBaseRef };

        var handle = await _factory.CreateAndRunAsync(context, request.TeamId, request.ActorUserId, session, cancellationToken).ConfigureAwait(false);

        return new LaunchTaskResult
        {
            RunId = handle.RunId,
            SessionId = session.SessionId,
            ProjectionKind = handle.ProjectionKind,
            Route = route,
            SurfaceKind = seed.SurfaceKind,
            LinkedEntity = seed.LinkedEntity,
        };
    }

    /// <summary>The grounding the run is primed with: on a CONTINUE, the session's prior-turn digest composed over any seed grounding; on a fresh launch, only the seed's own grounding (null for chat). The projection folds this into the agent prompt.</summary>
    private async Task<string?> ResolveGroundingAsync(TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken)
    {
        if (request.ContinueSessionId is not { } sessionId) return seed.GroundingContext;

        var priorTurns = await _sessionContext.BuildAsync(sessionId, request.TeamId, cancellationToken).ConfigureAwait(false);

        return ComposeGrounding(priorTurns, seed.GroundingContext);
    }

    /// <summary>Join the prior-turn digest and the seed's own grounding (either may be absent) into one block, digest first.</summary>
    private static string? ComposeGrounding(string? priorTurns, string? seedGrounding)
    {
        if (string.IsNullOrWhiteSpace(priorTurns)) return seedGrounding;
        if (string.IsNullOrWhiteSpace(seedGrounding)) return priorTurns;

        return $"{priorTurns}\n\n{seedGrounding}";
    }

    /// <summary>On a CONTINUE with a primary repo, the prior turn's produced branch for that repo (the projection clones the agent's workspace at it). Null on a fresh launch, an analysis-only run (no repo), or when no prior turn produced a branch (⇒ the repo's default branch — the safe fallback).</summary>
    private async Task<string?> ResolveBaseRefAsync(TaskLaunchRequest request, TaskLaunchSeed seed, ResolvedAgentProfile profile, CancellationToken cancellationToken)
    {
        if (request.ContinueSessionId is not { } sessionId) return null;

        if ((profile.RepositoryId ?? seed.RepositoryId) is not { } primaryRepoId) return null;

        return await _sessionBranches.ResolveStartRefAsync(sessionId, request.TeamId, primaryRepoId, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Maps the seed + the operator's effort/recipe/autonomy + safety-budget caps onto the router input. The router TIGHTENS the effort preset's caps with <c>CapsOverride</c> (null ⇒ preset-only, byte-identical).</summary>
    private static EffortRouteRequest BuildRouteRequest(TaskLaunchSeed seed, TaskLaunchRequest request) => new()
    {
        Seed = seed,
        RequestedEffort = request.RequestedEffort,
        RequestedRecipe = request.RequestedRecipe,
        CapsOverride = request.CapsOverride,
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
