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

        await EnsureModelsInTeamAsync(request, cancellationToken).ConfigureAwait(false);

        await EnsureAgentDefinitionsInTeamAsync(request, cancellationToken).ConfigureAwait(false);

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

        // Deep/Auto: the supervisor's brain model — the operator's pinned "Brain model" chip when set + usable, else
        // self-resolved so the decider has one instead of stopping turn-1. Inert (null) for every non-supervisor
        // projection — single-agent / map launches are byte-identical.
        var brainModelId = await ResolveSupervisorBrainModelAsync(request, route, cancellationToken).ConfigureAwait(false);

        var context = new TaskBuildContext { Seed = seed, Route = route, AgentProfile = profile, GroundingContext = grounding, BaseRefs = baseRefs, SupervisorBrainModelId = brainModelId, AllowedModelIds = request.AllowedModelIds, AllowedAgentDefinitionIds = request.AllowedAgentDefinitionIds, AcceptanceCriteria = request.AcceptanceCriteria, DecisionReviewMode = request.DecisionReviewMode, ReviewerModelId = request.ReviewerModelId };

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
    /// The supervisor's brain-model row id when the route projects an <c>agent.supervisor</c> node (the Deep/Auto lane).
    /// The operator's pinned "Brain model" chip (<c>Overrides.ModelCredentialModelId</c>) wins when it resolves to a real
    /// enabled/active team row a structured client can serve; otherwise — including a missing / disabled / cross-team /
    /// non-structured pin — it self-resolves from the team's pool, bounded to the providers a structured-LLM client
    /// actually serves (so it never trades NoBrainModelStop for NoModelStop). The chip drives the BRAIN only — the
    /// dispatched agents draw from the allowed pool (the brain authors per-agent), so this is the one place the pin lands.
    /// Null for every other projection (single-agent / map are byte-identical) and for an empty / structured-incapable
    /// pool (the builder then emits no brain — the honest fail-closed floor). Resolved ONCE here + baked into the
    /// immutable snapshot, so every turn + replay reads the same brain (a decide-time pick would drift if the pool changed).
    /// </summary>
    private async Task<Guid?> ResolveSupervisorBrainModelAsync(TaskLaunchRequest request, RoutePlan route, CancellationToken cancellationToken)
    {
        if (route.ProjectionKind != TaskProjectionKinds.Supervisor) return null;

        var structuredProviders = _llm.All.OfType<IStructuredLLMClient>().Select(c => c.Provider).ToList();

        if (structuredProviders.Count == 0) return null;

        if (request.Overrides.ModelCredentialModelId is { } pin
            && await _modelSelector.ResolvePinnedBrainRowIdAsync(request.TeamId, pin, structuredProviders, cancellationToken).ConfigureAwait(false) is { } pinnedBrain)
            return pinnedBrain;

        return await _modelSelector.SelectBrainRowIdAsync(request.TeamId, structuredProviders, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Validates EVERY operator-supplied allowed-model-pool row (<c>ModelCredentialModel</c> id) is a team-owned,
    /// enabled row under an active, non-deleted credential — the model analogue of <see cref="EnsureRepositoriesInTeamAsync"/>.
    /// A foreign / disabled / dead-credential row is an indistinguishable not-found (so a foreign row never leaks its
    /// existence), and ANY bad row fails the whole launch fail-closed BEFORE the session opens (no orphan). An empty
    /// pool ⇒ skip (all the team's models — byte-identical to no override). The dispatch-time pool guard still fails
    /// closed too; this just turns a silent "agent can't run" into a clear launch-time rejection.
    /// </summary>
    private async Task EnsureModelsInTeamAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        if (request.AllowedModelIds is not { Count: > 0 } pool) return;

        var ids = pool.ToHashSet();

        var inTeam = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => ids.Contains(m.Id) && m.Enabled
                && m.Credential.TeamId == request.TeamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (inTeam.Count != ids.Count)
            throw new KeyNotFoundException($"Model {string.Join(", ", ids.Except(inTeam))} not found or not accessible.");
    }

    /// <summary>
    /// Validates EVERY operator-supplied allowed-agent-pool row (<c>AgentDefinition</c> persona id) is a team-owned,
    /// non-deleted persona — the persona analogue of <see cref="EnsureModelsInTeamAsync"/>. A foreign / deleted persona
    /// is an indistinguishable not-found (existence never leaks), and ANY bad id fails the whole launch fail-closed
    /// BEFORE the session opens (no orphan). An empty pool ⇒ skip (all the team's personas — byte-identical). The
    /// dispatch-time pool gate still fails closed too; this turns a silent "can't dispatch" into a clear launch rejection.
    /// (Persona has no enabled/active flag — team + non-deleted is the whole predicate, matching <c>AgentDefinitionResolver</c>.)
    /// </summary>
    private async Task EnsureAgentDefinitionsInTeamAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        if (request.AllowedAgentDefinitionIds is not { Count: > 0 } pool) return;

        var ids = pool.ToHashSet();

        var inTeam = await _db.AgentDefinition.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.TeamId == request.TeamId && a.DeletedDate == null)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (inTeam.Count != ids.Count)
            throw new KeyNotFoundException($"Agent {string.Join(", ", ids.Except(inTeam))} not found or not accessible.");
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
            TimeoutSeconds = request.Overrides.TimeoutSeconds,
            IntegrateBranches = request.Overrides.IntegrateBranches,
            CwdMode = WorkspaceCwdModeWire.FromWire(request.Overrides.CwdMode),
            EnableMcp = request.Overrides.EnableMcp,
            AllowedTools = request.Overrides.AllowedTools is { Count: > 0 } tools ? tools : null,
            PushBranch = request.Overrides.PushBranch,
            OutputReviewMode = request.Overrides.OutputReviewMode,
            ReviewerModelId = request.Overrides.ReviewerModelId,
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
