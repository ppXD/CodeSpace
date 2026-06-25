using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Workflows.Llm;
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
    private readonly ISessionSummarizer _sessionSummarizer;
    private readonly ISessionBranchResolver _sessionBranches;
    private readonly IModelPoolSelector _modelSelector;
    private readonly ILLMClientRegistry _llm;
    private readonly CodeSpaceDbContext _db;

    public TaskLaunchService(ITaskLaunchSeedProviderRegistry seedProviders, IEffortRouter router, ITaskRunSnapshotFactory factory, IWorkSessionService sessions, ISessionContextBuilder sessionContext, ISessionSummarizer sessionSummarizer, ISessionBranchResolver sessionBranches, IModelPoolSelector modelSelector, ILLMClientRegistry llm, CodeSpaceDbContext db)
    {
        _seedProviders = seedProviders;
        _router = router;
        _factory = factory;
        _sessions = sessions;
        _sessionContext = sessionContext;
        _sessionSummarizer = sessionSummarizer;
        _sessionBranches = sessionBranches;
        _modelSelector = modelSelector;
        _llm = llm;
        _db = db;
    }

    public async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        var seed = await _seedProviders.Resolve(request.SurfaceKind).SeedAsync(request, cancellationToken).ConfigureAwait(false);

        await EnsureRepositoriesInTeamAsync(seed, request, cancellationToken).ConfigureAwait(false);

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

        // …and clone EACH repo (primary + related) at the prior turn's produced branch for it, so the follow-up builds
        // on earlier CODE (not just the narrative). Empty on a fresh launch / no repo / no prior branch ⇒ default branches.
        var baseRefs = await ResolveBaseRefsAsync(request, seed, profile, cancellationToken).ConfigureAwait(false);

        // Deep/Auto: self-resolve the supervisor's brain model so the decider has one instead of stopping turn-1. Inert
        // (null) for every non-supervisor projection — single-agent / map launches are byte-identical.
        var brainModelId = await ResolveSupervisorBrainModelAsync(route, request.TeamId, cancellationToken).ConfigureAwait(false);

        var context = new TaskBuildContext { Seed = seed, Route = route, AgentProfile = profile, GroundingContext = grounding, BaseRefs = baseRefs, SupervisorBrainModelId = brainModelId };

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

    /// <summary>
    /// The supervisor's brain-model row id when the route projects an <c>agent.supervisor</c> node (the Deep/Auto lane)
    /// and the operator pinned none — self-resolved from the team's pool, bounded to the providers a structured-LLM
    /// client actually serves (so it never trades NoBrainModelStop for NoModelStop). Null for every other projection
    /// (single-agent / map are byte-identical) and for an empty / structured-incapable pool (the builder then emits no
    /// brain — the honest fail-closed floor). Resolved ONCE here + baked into the immutable snapshot, so every turn +
    /// replay reads the same brain (a decide-time pick would drift if the pool changed mid-run).
    /// </summary>
    private async Task<Guid?> ResolveSupervisorBrainModelAsync(RoutePlan route, Guid teamId, CancellationToken cancellationToken)
    {
        if (route.ProjectionKind != TaskProjectionKinds.Supervisor) return null;

        var structuredProviders = _llm.All.OfType<IStructuredLLMClient>().Select(c => c.Provider).ToList();

        if (structuredProviders.Count == 0) return null;

        return await _modelSelector.SelectBrainRowIdAsync(teamId, structuredProviders, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The grounding the run is primed with: on a CONTINUE, the session's prior-turn digest composed over any seed grounding; on a fresh launch, only the seed's own grounding (null for chat). The projection folds this into the agent prompt.</summary>
    private async Task<string?> ResolveGroundingAsync(TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken)
    {
        if (request.ContinueSessionId is not { } sessionId) return seed.GroundingContext;

        // Fold any turns that scrolled out of the recent window into the thread's rolling summary BEFORE building the
        // digest, so a long thread's early context is preserved. Best-effort + fail-open (no model / error leaves it).
        await _sessionSummarizer.EnsureSummaryUpToDateAsync(sessionId, request.TeamId, cancellationToken).ConfigureAwait(false);

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

    /// <summary>On a CONTINUE, the prior turn's produced branch for EACH repo the run touches (primary + related) — the projection clones each repo's workspace at its own ref. Empty on a fresh launch, an analysis-only run (no repos), or when no prior turn produced a branch for any (⇒ default branches — the safe fallback). A repo absent from the map clones at its default.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ResolveBaseRefsAsync(TaskLaunchRequest request, TaskLaunchSeed seed, ResolvedAgentProfile profile, CancellationToken cancellationToken)
    {
        if (request.ContinueSessionId is not { } sessionId) return EmptyBaseRefs;

        var scopeRepoIds = new HashSet<Guid>();

        if ((profile.RepositoryId ?? seed.RepositoryId) is { } primaryRepoId) scopeRepoIds.Add(primaryRepoId);
        if (profile.RelatedRepositories is { } related) foreach (var r in related) scopeRepoIds.Add(r.RepositoryId);

        if (scopeRepoIds.Count == 0) return EmptyBaseRefs;

        return await _sessionBranches.ResolveStartRefsAsync(sessionId, request.TeamId, scopeRepoIds, cancellationToken).ConfigureAwait(false);
    }

    private static readonly IReadOnlyDictionary<Guid, string> EmptyBaseRefs = new Dictionary<Guid, string>();

    /// <summary>
    /// Validates EVERY repo the run touches — the primary (seed's, else request's) PLUS each related (multi-repo) repo —
    /// belongs to <c>request.TeamId</c>. A foreign / missing repo is a clear not-found (indistinguishable, so a foreign
    /// repo never leaks) and rejecting ANY one fails the whole launch fail-closed, BEFORE the session opens (no orphan).
    /// No repo named ⇒ skip (analysis-only is valid). Single-repo path is byte-identical (one id in, the same message out).
    /// </summary>
    private async Task EnsureRepositoriesInTeamAsync(TaskLaunchSeed seed, TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();

        if ((seed.RepositoryId ?? request.RepositoryId) is { } primary) ids.Add(primary);
        if (request.RelatedRepositories is { } related) foreach (var r in related) ids.Add(r.RepositoryId);

        if (ids.Count == 0) return;

        var inTeam = await _db.Repository.AsNoTracking()
            .Where(r => ids.Contains(r.Id) && r.TeamId == request.TeamId && r.DeletedDate == null)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (inTeam.Count != ids.Count)
            throw new KeyNotFoundException($"Repository {string.Join(", ", ids.Except(inTeam))} not found or not accessible.");
    }

    /// <summary>Maps the seed + the operator's effort/recipe/autonomy + safety-budget caps onto the router input. The router TIGHTENS the effort preset's caps with <c>CapsOverride</c> (null ⇒ preset-only, byte-identical).</summary>
    private static EffortRouteRequest BuildRouteRequest(TaskLaunchSeed seed, TaskLaunchRequest request) => new()
    {
        Seed = seed,
        RequestedEffort = request.RequestedEffort,
        RequestedRecipe = request.RequestedRecipe,
        CapsOverride = request.CapsOverride,
    };

    /// <summary>Pure mapping: the request overrides + (seed repo ?? request repo) + each related repo + the CLAMPED autonomy → the agent envelope the projection stamps. Every field optional, folding to agent.code's own defaults. Related repos require a primary (fail-loud, mirroring the agent.code node — a workspace has nowhere to anchor without one). Internal (not private) so the clamp + related-repo choke point is unit-pinned directly (InternalsVisibleTo), not only through integration coverage.</summary>
    internal static ResolvedAgentProfile BuildAgentProfile(TaskLaunchRequest request, TaskLaunchSeed seed, RoutePlan route)
    {
        var repositoryId = seed.RepositoryId ?? request.RepositoryId;
        var related = BuildRelatedRepositories(request.RelatedRepositories);

        if (related is { Count: > 0 } && repositoryId is null)
            throw new ArgumentException("Related repositories require a primary repository — name a primary repository, or remove the related ones.");

        return new ResolvedAgentProfile
        {
            RepositoryId = repositoryId,
            RelatedRepositories = related,
            Harness = request.Overrides.Harness,
            Model = request.Overrides.Model,
            AgentDefinitionId = request.Overrides.AgentDefinitionId,
            ModelCredentialId = request.Overrides.ModelCredentialId,
            ModelCredentialModelId = request.Overrides.ModelCredentialModelId,
            RunnerKind = request.Overrides.RunnerKind,
            AutonomyLevel = ClampAutonomy(request, route),
        };
    }

    /// <summary>Project the operator's typed related repos onto <see cref="WorkspaceRepositorySpec"/>s through the SHARED authoring path (Rule 7 — the same defaults the agent.code node + supervisor get). Null / empty ⇒ null, so a single-repo launch leaves <c>RelatedRepositories</c> unset (the projection omits the key — byte-identical).</summary>
    private static IReadOnlyList<WorkspaceRepositorySpec>? BuildRelatedRepositories(IReadOnlyList<TaskRelatedRepository>? related) =>
        related is { Count: > 0 }
            ? related.Select(r => AgentWorkspaceAuthoring.ToRelatedSpec(r.RepositoryId, r.Alias, r.Access)).ToList()
            : null;

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
