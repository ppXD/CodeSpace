using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Agents.Harnesses;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// The ASYNC half of the real executor (Rule 10 <c>.Spawn.cs</c>): <c>spawn</c> / <c>retry</c> stage K real
/// <c>agent.run</c> child runs + K <c>AgentRun</c> waits, then the node parks on them (the wait-for-all
/// barrier resumes the supervisor once every spawned agent terminates). Mirrors the engine's agent-run
/// staging (<c>WorkflowEngine.StageAgentRunAsync</c>) but K-at-once for the supervisor's per-turn fan-out —
/// reusing the SAME <c>AgentRun</c> wait kind + barrier, NOT a parallel fan-out.
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    /// <summary>Spawn: fan out one agent per planned subtask id, keyed <c>&lt;nodeId&gt;#turn{N}#{k}</c>. Parks on the K waits.</summary>
    private async Task<SupervisorExecution> ExecuteSpawnAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var spawn = Deserialize<SupervisorSpawnPayload>(decision.PayloadJson) ?? new SupervisorSpawnPayload();
        var subtasks = ResolvePlannedSubtasks(context);

        // A spawn that names NO unit is a malformed decision, and must be refused the way its retry twin already is
        // (BuildRejectedRetryOutcome) rather than accepted as a no-op. The decision schema requires only `kind`, so
        // `{"kind":"spawn"}` with no spawn object at all is schema-VALID; the projector then substitutes an empty
        // payload and this staged nothing while telling the model nothing. Observed dominating the decision-eval
        // lane: every scenario looping plan→spawn×7 into the turn cap, every spawn staging nothing.
        //
        // NOT applied to a spawn the SERVER emptied: the dependency clamp legitimately narrows an all-deferred
        // fan-out to zero, and calling that a malformed decision would tell the model to fix a defect it did not
        // commit. Attribution is the clamp's own stamp, because that is the only thing that actually discriminates:
        // the clamp writes it exactly when it deferred something, and returns untouched otherwise.
        //
        // A first attempt asked the dependency FRONTIER instead ("a plan with nothing blocked cannot have been
        // clamped") and silently did nothing — Blocked is every unfinished unit with an unmet edge, so ANY plan
        // declaring an edge suppressed the refusal, which is every plan these scenarios author. Run 31074294816
        // shipped that version and still recorded 120 accepted-empty spawns and zero refusals.
        if (spawn.SubtaskIds.Count == 0 && !SupervisorOutcome.HasDeferredSubtasks(decision.PayloadJson))
        {
            _logger.LogWarning("Supervisor REJECTED the spawn at turn {Turn} on node {NodeId} — the decision named no subtaskIds", context.TurnNumber, context.NodeId);
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(BuildRejectedSpawnOutcome(), AgentJson.Options));
        }

        // H2 (strict action identity): when a plan EXISTS, every spawned id must be one of ITS units — an unknown
        // id used to silently fall through BuildAgentTask's instruction chain all the way to the WHOLE GOAL (a
        // ghost agent re-running the entire task under a typo'd or stale-plan id, its results keyed to a unit the
        // plan never declared). The WHOLE spawn is rejected (never a partial filter — the positional
        // subtaskIds[i] ↔ agentResults[i] join must stay intact) with the reason + the declared universe, so the
        // decider's next turn re-authors against real ids. A run with NO plan keeps its pre-existing free-form
        // spawn semantics untouched (P+ plan-lineage formalizes that case).
        if (subtasks.Count > 0 && spawn.SubtaskIds.Where(id => !subtasks.ContainsKey(id)).ToList() is { Count: > 0 } unknown)
        {
            _logger.LogWarning("Supervisor REJECTED the spawn at turn {Turn} on node {NodeId} — it named subtask id(s) [{Unknown}] the current plan never declared; the plan's units are [{Declared}]", context.TurnNumber, context.NodeId, string.Join(", ", unknown), string.Join(", ", subtasks.Keys));
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(BuildUnknownSubtaskSpawnOutcome(unknown, subtasks.Keys), AgentJson.Options));
        }

        // A model-authored persona SLUG that resolves to nothing is a MODEL MISS, not an invariant breach: the capability
        // catalog advertises the field, the schema carries an example, and the model can misname it. So reject the WHOLE
        // spawn with a re-authorable reason — the same shape as an unknown subtask id above — instead of letting
        // ApplyDispatchPersonaAsync throw mid-stage. That throw terminalized the run: live-observed on four real-model
        // runs (2026-08-19 10:16 through 2026-08-20 01:12) dying byte-identically on the slug 'metis-coder' with
        // agents=0, AFTER the plan lookup and the dependency staging had both already succeeded.
        //
        // Checked UP FRONT, before any agent is staged, for the second reason the throw was wrong: a multi-agent spawn
        // whose second dispatch named a bad slug would stage the first agent and then die, leaving a partial fan-out
        // behind a failed run. IAgentDefinitionResolver.ResolveSlugAsync documents that a null "the caller decides
        // whether that is fail-closed or a fallback" — this is that decision, for the one caller a model can steer.
        if (await UnresolvablePersonaSlugsAsync(spawn, context, cancellationToken).ConfigureAwait(false) is { Count: > 0 } unknownPersonas)
        {
            _logger.LogWarning("Supervisor REJECTED the spawn at turn {Turn} on node {NodeId} — it authored persona slug(s) [{Slugs}] that resolve to no active persona in this team's library", context.TurnNumber, context.NodeId, string.Join(", ", unknownPersonas));
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(BuildUnknownPersonaSpawnOutcome(unknownPersonas), AgentJson.Options));
        }

        // The MODEL twin of the persona pre-flight above, and the last known run-killer of its class. A model-authored
        // per-agent model NAME the run cannot resolve used to reach ApplyDispatchModelAsync and THROW mid fan-out, after
        // the plan and the dependency staging had both already succeeded — the byte-identical failure #1535 fixed for
        // slugs. ScreenAuthoredModelsAsync splits the one null ResolveDispatchAsync returns into its two cases: a name
        // the team credentials nowhere is a MODEL MISS, rejected re-authorably here; a real team model merely OUTSIDE
        // the operator's pool stays a fail-closed throw (that boundary is governance, and is not re-authorable) — but
        // now raised BEFORE anything stages, so even the governance path can no longer leave a partial fan-out behind.
        if (await ScreenAuthoredModelsAsync(spawn, context, cancellationToken).ConfigureAwait(false) is { Count: > 0 } unknownModels)
        {
            _logger.LogWarning("Supervisor REJECTED the spawn at turn {Turn} on node {NodeId} — it authored model name(s) [{Models}] that resolve to no credentialed model of this team", context.TurnNumber, context.NodeId, string.Join(", ", unknownModels));
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(BuildUnknownModelSpawnOutcome(unknownModels), AgentJson.Options));
        }

        // The HARNESS twin of the two pre-flights above, and the same defect on the third model-authored axis. An
        // `agents[].harness` kind the run cannot admit used to reach ApplyDispatchHarnessPool inside the staging loop and
        // THROW, so an invented CLI name killed the run after the plan and the dependency staging had both succeeded.
        // ScreenAuthoredHarnesses splits the two things the old single gate said the same sentence about: a kind NO
        // registered adapter has is a MODEL MISS, rejected re-authorably here; a REGISTERED kind the operator kept out of
        // this run's pool stays a fail-closed throw (governance, not re-authorable) — raised here, before anything stages.
        if (ScreenAuthoredHarnesses(spawn, context) is { Count: > 0 } unknownHarnesses)
        {
            _logger.LogWarning("Supervisor REJECTED the spawn at turn {Turn} on node {NodeId} — it authored harness kind(s) [{Harnesses}] that no registered coding-agent adapter has", context.TurnNumber, context.NodeId, string.Join(", ", unknownHarnesses));
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(BuildUnknownHarnessSpawnOutcome(unknownHarnesses, AdmittedHarnessKinds(context)), AgentJson.Options));
        }

        // Fan out over the subtask ids (already clamped to the dependency-ready frontier when the decision was formed —
        // see SupervisorTurnService.ClampSpawnToDependencyFrontier — so the persisted payload's subtaskIds match the
        // staged agents one-for-one). For each, apply the model-authored per-agent dispatch override (L4 arc B) when the
        // spawn carries one keyed by that subtask id, else build a homogeneous profile clone (byte-identical to before).
        // The dispatch spec rides ALONGSIDE the task so the async stage can resolve its per-agent persona slug (P3) on a
        // FRESH stage only — a crash-recovery orphan reclaim reuses the already-resolved task and never re-resolves.
        //
        // S1 handoff: a subtask that DEPENDS on a prior producer is staged from that producer's recorded branch/patch
        // (never a fresh clone of the repository's default branch — the root cause of run 28fec923). Resolving that
        // staging is async (a manifest read, occasionally a real git integration), so this is a loop, not the prior
        // LINQ projection. ANY blocked subtask aborts the WHOLE spawn synchronously with zero agents (never a partial
        // fan-out): the positional subtaskIds[i] ↔ agentResults[i] join every dependency/merge/resolve reader relies on
        // would otherwise desync the moment one index is silently dropped.
        var tasks = new List<(AgentTask, SupervisorAgentDispatch?)>();
        var blocked = new List<DependencyBlock>();

        var contractHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var acceptanceUnits = new HashSet<string>(StringComparer.Ordinal);
        var deliveryUnits = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in spawn.SubtaskIds)
        {
            var spec = DispatchFor(spawn, id);
            var planned = subtasks.GetValueOrDefault(id);
            var repositoryId = ResolveTargetRepositoryId(spec, context);

            if (planned is not null) contractHashes[id] = SupervisorUnitContract.Hash(planned, spec?.GoalOverride, spec?.RepositoryId);

            if (planned is not null && SupervisorUnitContract.OwesAcceptance(planned)) acceptanceUnits.Add(id);
            if (planned is not null && SupervisorUnitContract.OwesDelivery(planned)) deliveryUnits.Add(id);

            var staging = await ResolveDependencyStagingAsync(DependsOnFor(planned, spec), repositoryId, context, cancellationToken).ConfigureAwait(false);

            if (staging.IsBlocked)
            {
                blocked.Add(new DependencyBlock(id, staging.BlockedReason!, staging.ConflictedFiles, staging.PreservedBranches));
                continue;
            }

            tasks.Add((BuildAgentTask(subtasks, id, spec?.GoalOverride, context, spec, staging), spec));
        }

        if (blocked.Count > 0)
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(BuildBlockedSpawnOutcome(blocked), AgentJson.Options));

        return await StageAgentsAndParkAsync(tasks, context, cancellationToken, contractHashes: contractHashes, acceptanceUnits: acceptanceUnits, deliveryUnits: deliveryUnits).ConfigureAwait(false);
    }

    /// <summary>A subtask's staging dependency: the model-authored <see cref="SupervisorAgentDispatch.BaseSubtaskId"/> override when present (narrows to ONE producer for this specific spawn), else the plan's own <c>DependsOn</c>. Empty when neither names a producer (byte-identical no-override path). Internal + static so the precedence is unit-pinned directly.</summary>
    internal static IReadOnlyList<string> DependsOnFor(SupervisorPlannedSubtask? planned, SupervisorAgentDispatch? spec) =>
        !string.IsNullOrWhiteSpace(spec?.BaseSubtaskId) ? new[] { spec!.BaseSubtaskId! } : planned?.DependsOn ?? Array.Empty<string>();

    /// <summary>The retry world-state-conservation precedence (P0-1): a resolved prior-attempt ref is strictly MORE specific than a plan-dependency handoff (it is THIS exact subtask's own committed work, not a producer's), so it wins whenever both resolve. No prior-attempt ref → the dependency staging stands unchanged (byte-identical to before P0-1). Internal + static so the precedence is unit-pinned directly, mirroring <see cref="DependsOnFor"/>.</summary>
    internal static DependencyStagingResult PreferPriorAttemptStaging(DependencyStagingResult priorAttemptStaging, DependencyStagingResult dependencyStaging) =>
        priorAttemptStaging.Ref is not null ? priorAttemptStaging : dependencyStaging;

    /// <summary>The subtask's target repository, resolved the SAME way <see cref="BuildTaskWithGoal"/> will resolve it — a pure pre-computation so dependency staging can look up the right repo's manifest before the task itself is built.</summary>
    private static Guid? ResolveTargetRepositoryId(SupervisorAgentDispatch? spec, SupervisorTurnContext context)
    {
        var boundRelated = AgentWorkspaceAuthoring.ParseRelatedRepositories(context.AgentProfile?.RelatedRepositories ?? default);

        return SupervisorRepoClamp.ClampPrimary(spec?.RepositoryId, context.AgentProfile?.RepositoryId, boundRelated);
    }

    /// <summary>One subtask a dependency-staging block withheld from this turn's spawn, with the loud reason + (when the block was an integration conflict) the conflicted files and the producers' own preserved branches.</summary>
    internal readonly record struct DependencyBlock(string SubtaskId, string Reason, IReadOnlyList<string> ConflictedFiles, IReadOnlyList<string> PreservedBranches);

    /// <summary>
    /// The synchronous zero-agent outcome for a spawn withheld by dependency staging — <c>agentRunIds</c>/<c>agentCount</c>
    /// stay in the SAME shape a no-op spawn already emits (so every existing "did this stage agents" reader is
    /// unaffected), plus <c>blockedSubtasks</c> naming why. When any block carried conflict detail, an <c>integration</c>
    /// block is ALSO recorded in the SAME shape <see cref="SupervisorOutcome.ReadIntegration"/> reads off a <c>merge</c> —
    /// so the EXISTING <c>resolve</c> verb (its widened <see cref="FindMostRecentConflictDecision"/>) can reconcile a
    /// staging-time conflict exactly as it reconciles a merge-time one, with no new escalation mechanism. Internal +
    /// static so the shape is unit-pinned against the SAME <see cref="SupervisorOutcome.ReadIntegration"/> reader.
    /// </summary>
    internal static object BuildBlockedSpawnOutcome(IReadOnlyList<DependencyBlock> blocked)
    {
        var conflicted = blocked.Where(b => b.ConflictedFiles.Count > 0 || b.PreservedBranches.Count > 0).ToList();

        return new
        {
            agentRunIds = Array.Empty<Guid>(),
            agentCount = 0,
            blockedSubtasks = blocked.Select(b => new { subtaskId = b.SubtaskId, reason = b.Reason }).ToList(),
            integration = conflicted.Count == 0 ? null : new
            {
                status = "Conflicted",
                outcomes = conflicted.SelectMany(b => b.PreservedBranches.Select(branch => new { label = b.SubtaskId, fallbackBranch = branch, conflictedFiles = b.ConflictedFiles })).ToList(),
                reason = "a dependent subtask's producers could not be auto-integrated onto one branch",
            },
        };
    }

    /// <summary>The model-authored per-agent dispatch for a subtask id (the FIRST matching <c>agents[]</c> entry — lenient on duplicates), or null. A spawn with no <c>agents[]</c> returns null for every id → byte-identical homogeneous fan-out.</summary>
    /// <summary>Every DISTINCT model-authored persona slug in this spawn that resolves to no active persona in the team. Empty when the spawn authored none (the run's own persona stands) or when every slug resolves.</summary>
    private async Task<IReadOnlyList<string>> UnresolvablePersonaSlugsAsync(SupervisorSpawnPayload spawn, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var authored = spawn.SubtaskIds.Select(id => NullIfBlank(DispatchFor(spawn, id)?.AgentDefinition)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var unresolvable = new List<string>();

        foreach (var slug in authored)
        {
            if (await _agentDefinitionResolver.ResolveSlugAsync(slug, context.TeamId, cancellationToken).ConfigureAwait(false) is null) unresolvable.Add(slug);
        }

        return unresolvable;
    }

    /// <summary>
    /// Screen every DISTINCT model-authored per-agent model NAME in this spawn UP FRONT — the model twin of
    /// <see cref="UnresolvablePersonaSlugsAsync"/>. <c>ResolveDispatchAsync</c> answers BOTH "no such credentialed model"
    /// and "credentialed, but not in this run's pool" with the same null, so the two are separated by asking a SECOND
    /// time UNBOUNDED (<c>allowedRowIds: null</c> — the same team-wide call <c>HarnessModelReconciler</c> makes):
    /// resolvable team-wide but not under the run's pool is the OPERATOR's boundary, so it THROWS
    /// <see cref="SupervisorModelAccessException"/> (fail-closed, never re-authorable); resolvable under neither is a
    /// name the team credentials nowhere — a MODEL MISS, RETURNED for a re-authorable rejection. Returns empty when the
    /// spawn authored no model (the profile's own model stands) or when every authored name resolves in-pool.
    /// <para>An unbounded run (null/empty <c>AllowedModelIds</c>) can never take the throw branch: the first query IS
    /// the unbounded one, so a null there re-nulls on the second and classifies as a miss.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> ScreenAuthoredModelsAsync(SupervisorSpawnPayload spawn, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var authored = spawn.SubtaskIds.Select(id => NullIfBlank(DispatchFor(spawn, id)?.Model)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var unresolvable = new List<string>();

        foreach (var name in authored)
        {
            if (await _modelSelector.ResolveDispatchAsync(context.TeamId, name, context.AllowedModelIds, cancellationToken).ConfigureAwait(false) is not null) continue;

            if (await _modelSelector.ResolveDispatchAsync(context.TeamId, name, allowedRowIds: null, cancellationToken).ConfigureAwait(false) is not null)
                throw new SupervisorModelAccessException($"agent.supervisor spawn requests model '{name}', which this team credentials but the operator did not admit to this run's allowed model pool.");

            unresolvable.Add(name);
        }

        return unresolvable;
    }

    /// <summary>
    /// Screen every DISTINCT model-authored per-agent HARNESS kind in this spawn UP FRONT — the harness sibling of
    /// <see cref="UnresolvablePersonaSlugsAsync"/> and <see cref="ScreenAuthoredModelsAsync"/>, and the same two-case
    /// split. A kind NO registered <see cref="IAgentHarness"/> has is a name the brain invented: RETURNED, for a
    /// re-authorable rejection. A REGISTERED kind the operator kept out of <see cref="SupervisorTurnContext.AllowedAgentKinds"/>
    /// is the OPERATOR's boundary, so it THROWS <see cref="SupervisorAgentAccessException"/> (fail-closed, never
    /// re-authorable) — before any agent stages, so the governance path cannot leave a partial fan-out behind either.
    /// Returns empty when the spawn authored no harness (the profile's own stands) or every authored kind is admitted.
    /// <para>Registration is checked against the FULL registry, not the clamped pool, precisely so the two cases stay
    /// distinguishable: clamping first would report an un-admitted real harness as an invented one.</para>
    /// </summary>
    private IReadOnlyList<string> ScreenAuthoredHarnesses(SupervisorSpawnPayload spawn, SupervisorTurnContext context)
    {
        var authored = spawn.SubtaskIds.Select(id => NullIfBlank(DispatchFor(spawn, id)?.Harness)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var unregistered = new List<string>();

        foreach (var kind in authored)
        {
            if (!_harnesses.All.Any(h => string.Equals(h.Kind, kind, StringComparison.OrdinalIgnoreCase)))
            {
                unregistered.Add(kind);
                continue;
            }

            if (context.AllowedAgentKinds is { Count: > 0 } pool && !pool.Contains(kind, StringComparer.OrdinalIgnoreCase))
                throw new SupervisorAgentAccessException($"agent.supervisor spawn requests harness '{kind}', which is a registered adapter but the operator did not admit to this run's allowed harness pool.");
        }

        return unregistered;
    }

    /// <summary>The harness kinds this run may spawn, as the capability catalog renders them (<c>LlmSupervisorDecider</c> clamps the same way) — so a rejection names the SAME universe the brain was shown, never the wider registry.</summary>
    private IReadOnlyList<string> AdmittedHarnessKinds(SupervisorTurnContext context) =>
        AgentHarnessPool.Clamp(_harnesses.All, context.AllowedAgentKinds).Select(h => h.Kind).ToList();

    private static SupervisorAgentDispatch? DispatchFor(SupervisorSpawnPayload spawn, string subtaskId) =>
        spawn.Agents?.FirstOrDefault(a => a.SubtaskId == subtaskId);

    /// <summary>
    /// P0-2 (action schema validation): the retry payload named no plan-local subtask id — either the model omitted
    /// the <c>retry</c> sub-object entirely (schema-legal; only <c>kind</c> is root-required) or supplied a blank
    /// <c>subtaskId</c> (the schema places no <c>minLength</c> on it). Unlike an empty spawn (which a legitimate
    /// dependency-clamp can ALSO narrow to zero), nothing ever clamps a retry's <c>subtaskId</c> — an empty one
    /// reaching here is unambiguously a malformed decision, so it is REJECTED with a specific reason rather than
    /// silently no-opped, mirroring <see cref="ResolveSkipReason"/>'s named-reason precedent. The existing
    /// no-progress watchdog already counts this turn's empty <c>agentResults</c> as a stall tick; this only makes
    /// the reason legible to the decider on ITS next turn.
    /// </summary>
    /// <summary>
    /// The rejection outcome for a spawn that named NO unit — the twin of <see cref="BuildRejectedRetryOutcome"/>,
    /// in the same <c>{verb: "rejected", reason}</c> shape the decider's correction block renders. Reached when the
    /// model emits <c>kind: "spawn"</c> without a spawn payload (schema-valid: only <c>kind</c> is required) or with
    /// an empty <c>subtaskIds</c>, and NOT when the dependency clamp emptied it.
    /// </summary>
    internal static object BuildRejectedSpawnOutcome() => new
    {
        agentRunIds = Array.Empty<Guid>(),
        agentCount = 0,
        spawn = "rejected",
        reason = "the spawn decision named no subtaskIds — a spawn must name the plan-local subtask id(s) to fan out",
    };

    internal static object BuildRejectedRetryOutcome() => new
    {
        retry = "rejected",
        reason = "the retry decision named no subtaskId — a retry must name the plan-local subtask id to re-run",
    };

    /// <summary>
    /// H2: the zero-agent rejection outcome for a spawn naming ids the current plan never declared — the SAME
    /// <c>agentRunIds</c>/<c>agentCount</c> shape a no-op spawn already emits (every "did this stage agents" reader
    /// is unaffected), plus the rejected ids and the declared universe so the decider re-authors against REAL ids
    /// instead of guessing. Mirrors <see cref="BuildBlockedSpawnOutcome"/>'s legible-reason precedent.
    /// </summary>
    internal static object BuildUnknownSubtaskSpawnOutcome(IReadOnlyList<string> unknown, IEnumerable<string> declared) => new
    {
        agentRunIds = Array.Empty<Guid>(),
        agentCount = 0,
        spawn = "rejected",
        reason = $"the spawn named subtask id(s) the current plan never declared: [{string.Join(", ", unknown)}] — the plan's units are [{string.Join(", ", declared)}]; re-author the spawn against those ids (or re-plan first)",
    };

    /// <summary>The rejection outcome for a spawn authoring a persona slug this team's library does not hold — re-authorable, unlike the throw it replaced, which killed the run after the plan and the dependency staging had both already succeeded.</summary>
    internal static object BuildUnknownPersonaSpawnOutcome(IReadOnlyList<string> unknown) => new
    {
        agentRunIds = Array.Empty<Guid>(),
        agentCount = 0,
        spawn = "rejected",
        reason = $"the spawn authored persona slug(s) that no active persona in this team's library has: [{string.Join(", ", unknown)}] — re-author with a slug the capability catalog lists, or OMIT agentDefinition entirely to let the run's own persona stand",
    };

    /// <summary>The rejection outcome for a spawn authoring a model name no credentialed model of this team has — re-authorable, unlike the throw it replaced, which killed the run mid fan-out. An out-of-POOL (but real) model is NOT routed here: that boundary is the operator's, and stays a fail-closed throw.</summary>
    internal static object BuildUnknownModelSpawnOutcome(IReadOnlyList<string> unknown) => new
    {
        agentRunIds = Array.Empty<Guid>(),
        agentCount = 0,
        spawn = "rejected",
        reason = $"the spawn authored model name(s) no credentialed model of this team has: [{string.Join(", ", unknown)}] — re-author with a model the capability catalog lists, or OMIT model entirely to let the run's own model apply",
    };

    /// <summary>The rejection outcome for a spawn authoring a harness kind no registered adapter has — re-authorable, unlike the throw it replaced, which killed the run mid fan-out. A REGISTERED but un-admitted kind is NOT routed here: that boundary is the operator's, and stays a fail-closed throw. <paramref name="admitted"/> is the same clamped catalog the brain was shown, so the reason names a universe it can actually pick from.</summary>
    internal static object BuildUnknownHarnessSpawnOutcome(IReadOnlyList<string> unknown, IReadOnlyList<string> admitted) => new
    {
        agentRunIds = Array.Empty<Guid>(),
        agentCount = 0,
        spawn = "rejected",
        reason = $"the spawn authored harness kind(s) no registered coding-agent adapter has: [{string.Join(", ", unknown)}] — re-author with one of [{string.Join(", ", admitted)}], or OMIT harness entirely to let the run's own harness stand",
    };

    /// <summary>H2: the rejection outcome for a retry naming an id the current plan never declared — without this the instruction chain fell through to the WHOLE GOAL (a ghost re-run of the entire task under a stale or typo'd id).</summary>
    internal static object BuildUnknownSubtaskRetryOutcome(string subtaskId, IEnumerable<string> declared) => new
    {
        retry = "rejected",
        reason = $"the retry named subtask id '{subtaskId}', which the current plan never declared — the plan's units are [{string.Join(", ", declared)}]; retry one of those (or re-plan first)",
    };

    /// <summary>Retry: re-run ONE prior subtask as a FRESH agent run (a new Attempt), optionally with a revised instruction. Same stage-K-waits + barrier as spawn (here K = 1).</summary>
    private async Task<SupervisorExecution> ExecuteRetryAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var retry = Deserialize<SupervisorRetryPayload>(decision.PayloadJson);
        var subtasks = ResolvePlannedSubtasks(context);

        if (retry == null || string.IsNullOrWhiteSpace(retry.SubtaskId))
        {
            _logger.LogWarning("Supervisor REJECTED the retry at turn {Turn} on node {NodeId} — the decision named no subtaskId", context.TurnNumber, context.NodeId);
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(BuildRejectedRetryOutcome(), AgentJson.Options));
        }

        // H2 (strict action identity): a retry of an id the current plan never declared used to fall through the
        // instruction chain to the WHOLE GOAL — a ghost re-run under a stale-plan or typo'd id. Reject with the
        // declared universe instead (a run with NO plan keeps its pre-existing semantics; see the spawn's twin).
        if (subtasks.Count > 0 && !subtasks.ContainsKey(retry.SubtaskId))
        {
            _logger.LogWarning("Supervisor REJECTED the retry at turn {Turn} on node {NodeId} — it named subtask id '{SubtaskId}', which the current plan never declared; the plan's units are [{Declared}]", context.TurnNumber, context.NodeId, retry.SubtaskId, string.Join(", ", subtasks.Keys));
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(BuildUnknownSubtaskRetryOutcome(retry.SubtaskId, subtasks.Keys), AgentJson.Options));
        }

        // S1 handoff applies to a retry exactly as it does to a fresh spawn — a producer may have pushed a NEW branch
        // since this subtask's original attempt (e.g. it was itself retried), so re-resolving staging here (rather than
        // reusing whatever the original attempt saw) keeps the retry building on the CURRENT producer state.
        var planned = subtasks.GetValueOrDefault(retry.SubtaskId);
        var repositoryId = ResolveTargetRepositoryId(null, context);

        var staging = await ResolveDependencyStagingAsync(DependsOnFor(planned, null), repositoryId, context, cancellationToken).ConfigureAwait(false);

        if (staging.IsBlocked)
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(BuildBlockedSpawnOutcome(new[] { new DependencyBlock(retry.SubtaskId, staging.BlockedReason!, staging.ConflictedFiles, staging.PreservedBranches) }), AgentJson.Options));

        // D1 (retry-resume): a retry CONTINUES the failed attempt's conversation instead of restarting the subtask cold —
        // find the prior attempt of THIS subtask in THIS run FIRST (the executor resolves any ref, the Claude harness
        // restores the transcript at the retry's own cwd). No prior resumable attempt ⇒ byte-identical cold-start.
        var prior = await _agentRuns.FindResumableSubtaskAttemptAsync(context.TeamId, context.SupervisorRunId, retry.SubtaskId, cancellationToken).ConfigureAwait(false);

        // P0-1 (retry world-state conservation): the retried subtask's OWN prior attempt may have already pushed a
        // branch — that continuity is MORE specific than a plan dependency's handoff, so it wins the clone ref when
        // both resolve (the rare case a retried subtask also declares a DependsOn). The git-state lookup MUST key off
        // the SAME attempt whose conversation is being resumed (never a separately-resolved "latest attempt") so the
        // resume hint's honesty claim always describes what it actually restores; only when there is NO resumable
        // conversation at all does the literal latest attempt (by decision order) stand in, since there is then no
        // conversation-honesty concern to reconcile against. No prior attempt / no pushed branch → NoOverride, and the
        // plan-dependency staging (if any) stands unchanged.
        var priorAgentRunId = prior?.AgentRunId ?? SupervisorDependencyGate.LatestAgentRunId(context, retry.SubtaskId);
        var priorAttemptStaging = await ResolvePriorAttemptStagingAsync(priorAgentRunId, repositoryId, context, cancellationToken).ConfigureAwait(false);
        var effectiveStaging = PreferPriorAttemptStaging(priorAttemptStaging, staging);

        // P5-2: ONE lookup of the prior attempt's folded result feeds BOTH the failure-diagnosis handoff and the
        // A2 escalation trigger — keyed to the same attempt whose conversation/world-state this retry continues.
        var priorResult = SupervisorOutcome.FindResultByAgentRunId(context.PriorDecisions, priorAgentRunId);

        var builtTask = ApplyPriorFailureDiagnosis(BuildAgentTask(subtasks, retry.SubtaskId, retry.RevisedInstruction, context, staging: effectiveStaging), priorResult);

        var plannedUnit = subtasks.GetValueOrDefault(retry.SubtaskId);
        var retryContractHashes = plannedUnit is not null
            ? new Dictionary<string, string>(StringComparer.Ordinal) { [retry.SubtaskId] = SupervisorUnitContract.Hash(plannedUnit, retry.RevisedInstruction, repositoryOverride: null) }
            : null;
        var retryDeliveryUnits = plannedUnit is not null && SupervisorUnitContract.OwesDelivery(plannedUnit) ? new HashSet<string>(StringComparer.Ordinal) { retry.SubtaskId } : null;

        var (escalatedTask, escalation) = await ApplyRetryEscalationAsync(builtTask, priorResult, context, cancellationToken).ConfigureAwait(false);

        var task = ApplyRetryDisposition(escalatedTask, prior, priorResult, workspaceHasPriorWork: effectiveStaging.Ref is not null);

        if (AgentRetryCauses.Classify(priorResult?.Error) == AgentRetryCauses.GatewayFormatFault)
            _logger.LogWarning("Supervisor retry of subtask {SubtaskId}: the prior attempt died on a gateway FORMAT fault — retrying FRESH (a conversation replay re-triggers the fault) with extended thinking disabled ({EnvVar}=0)", retry.SubtaskId, AgentRetryCauses.MaxThinkingTokensEnvVar);

        return await StageAgentsAndParkAsync(new List<(AgentTask, SupervisorAgentDispatch?)> { (task, null) }, context, cancellationToken, escalation, retryContractHashes, retryDeliveryUnits).ConfigureAwait(false);
    }

    /// <summary>
    /// A2 (P4-2) — raise this retry's model floor when the run's OWN evidence says the prior attempt's model was
    /// insufficient (a self-report/acceptance-grade contradiction, or the run one no-progress decision away from its
    /// force-stop cap): <see cref="SupervisorRetryEscalation.EscalationReason"/> names WHY, <see cref="ResolveEscalatedModelAsync"/>
    /// picks the strongest credentialed candidate above the prior model's own effective tier. Skipped entirely once
    /// the run is already over its cost cap (never spend into a cap that is about to force-stop it anyway) — the SAME
    /// "EXCEEDS" predicate the turn loop's own cost-cap force-stop uses. No trigger, no candidate, or already-capped
    /// → the task's ordinary model resolution (the profile's pin, or none) passes through UNCHANGED, escalation null.
    /// </summary>
    private async Task<(AgentTask Task, SupervisorRetryEscalationOutcome? Escalation)> ApplyRetryEscalationAsync(AgentTask builtTask, SupervisorAgentResult? priorResult, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        if (context.MaxCostUsd is { } cap && context.RunSpendUsd > cap) return (builtTask, null);

        // B5 (A2 ruling): a contradiction graded by an oracle a human has since AMENDED is stale evidence — the
        // self-report never disagreed with the CO-SIGNED check, only with the dead one. Escalating the retry's
        // model tier on it would spend real money on a verdict everyone agrees was wrong. The no-progress
        // proximity trigger stays live (it reads the run's cadence, not the dead oracle's verdict).
        var contradiction = SupervisorAmendObligation.IsOutstanding(context, builtTask.SubtaskId) ? null : priorResult?.Contradiction;

        var reason = SupervisorRetryEscalation.EscalationReason(contradiction, context.NoProgressDecisions, context.MaxNoProgressDecisions);

        if (reason is null) return (builtTask, null);

        var picked = await ResolveEscalatedModelAsync(priorResult?.Model, context, cancellationToken).ConfigureAwait(false);

        // D3: nothing in the (bounded) pool beats the prior model's tier — a one-model team, or a run already at the
        // top. The retry's ordinary model resolution stands UNTOUCHED, but the attempt is RECORDED: previously this
        // returned null and the brain saw a still-failing retry with no hint that reaching higher had already been
        // tried and found impossible, which reads as "nobody tried" and invites the same retry again.
        if (picked is null) return (builtTask, new SupervisorRetryEscalationOutcome { From = priorResult?.Model, To = null, Reason = reason });

        return (builtTask with { Model = picked }, new SupervisorRetryEscalationOutcome { From = priorResult?.Model, To = picked, Reason = reason });
    }

    /// <summary>The escalated candidate pool: every ENABLED model on an ACTIVE, non-deleted credential of this team, bounded by <see cref="SupervisorTurnContext.AllowedModelIds"/> (empty = every team row) — the SAME pool + bound <c>ApplyDispatchModelAsync</c>'s own <c>ResolveDispatchAsync</c> will re-validate the picked name against, so an escalated pick can never be a phantom the pool gate then rejects.</summary>
    private async Task<string?> ResolveEscalatedModelAsync(string? priorModelName, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var query = _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled && m.Credential.TeamId == context.TeamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active);

        if (context.AllowedModelIds is { Count: > 0 } allowed)
            query = query.Where(m => allowed.Contains(m.Id));

        var rows = await query
            .Select(m => new { m.ModelId, m.IsDefault, m.CapabilityTier, m.ProbedCapabilityTier })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return SupervisorRetryEscalation.PickStrongerModel(rows, m => m.IsDefault, m => m.ProbedCapabilityTier, m => m.CapabilityTier, m => m.ModelId, priorModelName)?.ModelId;
    }

    /// <summary>
    /// Cause-aware retry disposition (the pure fork, unit-pinned like <see cref="ApplyResumeRecord"/>): a prior
    /// attempt that died on a gateway FORMAT fault retries FRESH with extended thinking disabled — resuming would
    /// replay the exact history that re-triggers the fault, burning a full attempt to relearn it. Every other
    /// shape keeps today's semantics byte-identically: resume when a resumable session exists, cold-start when not.
    /// World-state continuity (the prior branch tip staging) is decided elsewhere and stays UNCHANGED either way —
    /// the degrade drops the broken conversation, never the preserved work.
    /// </summary>
    internal static AgentTask ApplyRetryDisposition(AgentTask task, ResumableSession? prior, SupervisorAgentResult? priorResult, bool workspaceHasPriorWork)
    {
        if (AgentRetryCauses.Classify(priorResult?.Error) == AgentRetryCauses.GatewayFormatFault)
            return task with { Environment = AgentRetryCauses.WithThinkingDisabled(task.Environment) };

        return prior is null ? task : ApplyResumeRecord(task, prior, workspaceHasPriorWork);
    }

    /// <summary>The pure fold of a resumable prior attempt onto the task: always stamps the session/transcript, and — ONLY when <paramref name="workspaceHasPriorWork"/> is false — appends the honest-redo line so the hint's truth value always matches the actual git state. Internal + static so the honesty branch is unit-pinned directly.</summary>
    internal static AgentTask ApplyResumeRecord(AgentTask task, ResumableSession prior, bool workspaceHasPriorWork)
    {
        var resumed = task with { ResumeFromSessionId = prior.SessionId, RestoredTranscript = prior.InlineTranscript, RestoredTranscriptArtifactId = prior.TranscriptArtifactId };

        return workspaceHasPriorWork ? resumed : resumed with { Goal = AgentRetryContinuity.WithHonestNoContinuityHint(resumed.Goal) };
    }

    /// <summary>
    /// P5-2 (diagnosis-driven repair): fold the prior attempt's FAILED acceptance diagnosis — the check's detail +
    /// the bounded output tail the fold stamped — into the retried agent's goal, so the worker's first move is
    /// fixing what the oracle NAMED instead of re-running the suite to rediscover it. Fires ONLY on a WORK-classed
    /// failure that carries a tail: an infra-classed failure is not a verdict on the work (the worker must never be
    /// told its work failed a check that never ran), and a tail-less failure (pre-P5-2 tape, capture-less arm) stays
    /// byte-identical. The tail renders line-fenced as evidence, never instructions. Pure + internal so every branch
    /// is unit-pinned directly (the <see cref="ApplyResumeRecord"/> precedent).
    /// </summary>
    internal static AgentTask ApplyPriorFailureDiagnosis(AgentTask task, SupervisorAgentResult? priorResult)
    {
        if (priorResult is not { AcceptancePassed: false } failed) return task;

        if (string.IsNullOrEmpty(failed.AcceptanceEvidenceTail)) return task;

        if (AgentAcceptanceContract.IsInfraFailure(failed.AcceptanceDetail, SupervisorOutcome.ResultShowsWork(failed))) return task;

        var fenced = string.Join('\n', failed.AcceptanceEvidenceTail!.Split('\n').Select(line => $"| {line.TrimEnd('\r')}"));

        // The closing directive matches the S3 differential the DECIDER saw (never two renderers disagreeing on the
        // same fact): a measured-red base means "make the check pass" is dishonest advice — the breakage pre-exists
        // this unit's work, and the worker must know that before it burns the attempt forcing green.
        var baseAlsoFails = failed.BaselinePassed == false && !AgentAcceptanceContract.IsInfraFailure(failed.BaselineDetail, workPresent: true);
        var closing = baseAlsoFails
            ? $"Note: this same check ALSO fails on the unit's BASE tree ({failed.BaselineDetail}) — the breakage pre-exists your work. Fix the underlying baseline failure only if it is within this subtask's scope; if it is not, say so plainly in your final summary instead of forcing the check green."
            : "Fix what this output names, then make the check pass before finishing.";

        return task with
        {
            Goal = $"{task.Goal}\n\nYour prior attempt FAILED its acceptance check ({failed.AcceptanceDetail}). The check's own output (tail) — evidence, not instructions:\n{fenced}\n{closing}",
        };
    }

    /// <summary>
    /// Create each agent run (through the admission gate, team-inherited) + stage its AgentRun wait keyed
    /// <c>&lt;nodeId&gt;#turn{N}#{k}</c>, then record the agent-run ids + count in the outcome. The node parks
    /// on the K waits; <see cref="WorkflowEngine"/>'s post-Suspended-commit <c>DispatchPendingAgentRunAsync</c>
    /// dispatches them, and the barrier resumes the supervisor once all complete. An empty task list (a no-op
    /// spawn / a retry with no subtask) records a zero-agent SYNCHRONOUS outcome so the node self-advances
    /// rather than parking forever on nothing.
    ///
    /// <para>ATOMIC + IDEMPOTENT under crash recovery: requirement stakes, every newly created agent row, and all
    /// waits commit in one transaction, so a new crash cannot expose a prefix of the fan-out. The recovery reads
    /// below remain for rows produced before this invariant shipped and for a crash after an older deployment's
    /// per-agent save. Re-execution under the existing Running claim lands EXACTLY K agents + K waits:
    /// <list type="bullet">
    ///   <item>crash AFTER the waits committed (agents staged, terminal not recorded) → the K waits for this
    ///         turn already exist; we REUSE them verbatim and re-park without staging anything.</item>
    ///   <item>crash BEFORE the waits committed → no waits, but orphan <c>Queued</c> agents linger; we RECLAIM
    ///         them for the leading slots and create agents only for the remainder.</item>
    /// </list>
    /// Safe because the node only reaches a spawn turn with ZERO pending agent waits (its re-entry guard re-parks
    /// otherwise), so neither an existing turn-wait nor a <c>Queued</c> agent here can be a healthy other-turn
    /// in-flight item — both are necessarily THIS decision's crash residue.</para>
    /// </summary>
    private async Task<SupervisorExecution> StageAgentsAndParkAsync(IReadOnlyList<(AgentTask Task, SupervisorAgentDispatch? Spec)> tasks, SupervisorTurnContext context, CancellationToken cancellationToken, SupervisorRetryEscalationOutcome? escalation = null, IReadOnlyDictionary<string, string>? contractHashes = null, IReadOnlyCollection<string>? acceptanceUnits = null, IReadOnlyCollection<string>? deliveryUnits = null)
    {
        if (tasks.Count == 0)
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(new { agentRunIds = Array.Empty<Guid>(), agentCount = 0, note = "no subtasks to spawn" }, AgentJson.Options));

        var existingWaitAgentIds = await ExistingTurnWaitAgentIdsAsync(context, cancellationToken).ConfigureAwait(false);

        if (existingWaitAgentIds.Count > 0)
            return ReparkOnExistingWaits(context, existingWaitAgentIds);

        // W-hard 2a: ATOMIC wave admission — every attempt of this wave reserves its budget slice
        // (cap ÷ total-spawn cap, the config-derived natural estimate) BEFORE anything stages, all-or-nothing:
        // one rejection releases the wave's fresh reservations and returns a budget-blocked outcome the decider
        // can read (mirroring the dependency-block precedent — positional integrity is never truncated mid-wave).
        // Scope keys are the per-spawn iteration keys, so a crash-replayed staging lands on its own reservations
        // (admitted as already-reserved). An uncapped run (no MaxCostUsd) reserves nothing — same authority as
        // the realized-spend bound, which stays the user-facing stop.
        // D1 fail-CLOSED, BEFORE any reservation: a wave that would run a model nobody can price cannot be admitted
        // under a cost cap — its spend folds back as $0, so the cap it is admitted against would never trip. Blocking
        // is the same shape as the ledger's own refusal below (a synchronous budget-blocked outcome the decider reads,
        // never a truncated mid-wave stage), and the reason NAMES the model + the remedy. An unnamed model is the
        // harness default, which this layer never knew — it is not an unpriced pool pick and is not blocked here; the
        // tape fold catches whatever it turns out to have been (SupervisorBounds.PostDecision).
        //
        // The outcome records `unpricedModel` DURABLY, because blocking the wave alone would dead-end: the run's
        // unpriced signal is otherwise folded from REALIZED spend, and a wave that never ran realized none — so the
        // bound never fired and the run just re-decided until the no-progress cap, reporting a stall instead of the
        // missing price. The fold re-reads this name and re-prices it, so pricing the model while the run is parked
        // clears the block on the next wake rather than pinning it forever.
        if (context.MaxCostUsd is { } cap && tasks.Select(t => t.Task.Model).FirstOrDefault(m => UnpricedModelUnderCap.Blocks(m, cap, context.ModelPrices)) is { } unpriced)
        {
            _logger.LogWarning("Budget admission blocked a {Count}-agent wave on run {RunId}: model {Model} has no price under the ${Cap} cost cap", tasks.Count, context.SupervisorRunId, unpriced, cap);

            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(new
            {
                budgetBlocked = tasks.Select(t => t.Task.SubtaskId).ToArray(),
                reason = UnpricedModelUnderCap.Detail(unpriced, cap),
                unpricedModel = unpriced,
                capUsd = cap,
            }, AgentJson.Options));
        }

        if (context.MaxCostUsd is { } capUsd && context.SupervisorRunId != Guid.Empty && context.TeamId != Guid.Empty)
        {
            var estimate = capUsd / Math.Max(context.MaxTotalSpawns ?? SupervisorLane.DefaultMaxTotalSpawns, 1);
            var reservedKeys = new List<string>();

            for (var k = 0; k < tasks.Count; k++)
            {
                var scopeKey = $"{(string.IsNullOrEmpty(context.NodeId) ? "sup" : context.NodeId)}#turn{context.TurnNumber}#{k}";
                var admission = await _budget.ReserveAsync(context.SupervisorRunId, context.TeamId, "agent-attempt", scopeKey, estimate, capUsd, priceVersion: "realized-v1", parentReservationId: null, expiresAt: null, cancellationToken).ConfigureAwait(false);

                if (!admission.Admitted)
                {
                    foreach (var key in reservedKeys)
                        await _budget.ReleaseAsync(context.SupervisorRunId, context.TeamId, "agent-attempt", key, cancellationToken).ConfigureAwait(false);

                    _logger.LogWarning("Budget admission blocked a {Count}-agent wave on run {RunId}: {Reason}", tasks.Count, context.SupervisorRunId, admission.Reason);

                    return SupervisorExecution.Synchronous(JsonSerializer.Serialize(new
                    {
                        budgetBlocked = tasks.Select(t => t.Task.SubtaskId).ToArray(),
                        reason = admission.Reason,
                        committedUsd = admission.CommittedUsd,
                        capUsd = admission.CapUsd,
                    }, AgentJson.Options));
                }

                reservedKeys.Add(scopeKey);
            }
        }

        var orphans = await ReclaimableOrphanAgentIdsAsync(context, cancellationToken).ConfigureAwait(false);

        // P1a identity: every staged attempt carries the ATOMIC WorkUnitRef of the plan that dispatched it, read
        // from the latest plan DECISION's own recorded ref on the tape — an immutable fact bound at plan
        // execution, never a mutable "current plan" query, so a crash replay / reconciler re-dispatch / zombie
        // resume can never re-derive a different plan for an already-persisted decision. A plan-less dispatch
        // (or a pre-P1a tape whose plan decisions carry no ref) stamps nothing — null-omitted, byte-identical.
        var planRef = context.PriorDecisions
            .Where(d => d.DecisionKind == SupervisorDecisionKinds.Plan)
            .OrderBy(d => d.Sequence)
            .Select(d => SupervisorOutcome.ReadPlanRef(d.OutcomeJson))
            .LastOrDefault(r => r is not null);

        if (planRef is { } plan)
            tasks = tasks.Select(t => (t.Task with
            {
                WorkUnit = string.IsNullOrEmpty(t.Task.SubtaskId) ? null : new Messages.Contracts.WorkUnitRef
                {
                    WorkPlanId = plan.WorkPlanId,
                    PlanVersion = plan.Version,
                    UnitId = t.Task.SubtaskId,
                    // P1b: the EFFECTIVE contract's canonical hash (dispatch overrides included) — the content
                    // identity receipts and ReceiptAdmission bind to. Null when the unit has no known contract.
                    ContractHash = contractHashes?.GetValueOrDefault(t.Task.SubtaskId),
                },
            }, t.Spec)).ToList();

        // The whole authorization wave is one database commit: requirements, every fresh AgentRun, and every wait.
        // AgentRunService.CreateAsync deliberately SaveChanges per run, but those flushes stay private inside this
        // transaction until ALL K slots and waits exist. A cap rejection, resolver fault, or process disconnect rolls
        // the wave back to zero visible residue; replay never observes a prefix and mistakes it for a complete wave.
        await using var stagingTransaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // P2a-2 (R): the staged units' acceptance obligations become durable requirement rows AT AUTHORIZATION —
        // the composer reads these, never re-derives them from the tape. Upsert-idempotent: a crash-replayed
        // staging lands on the same (run, kind, ref) rows. Model-authored oracles carry ModelProposal authority
        // honestly (an obligation can only add Unknown/park — authority gates RECEIPTS, not requirements).
        // Best-effort: a ledger fault must never strand the staging itself.
        if (planRef is not null && contractHashes is { Count: > 0 } && context.SupervisorRunId != Guid.Empty && context.TeamId != Guid.Empty)
        {
            const string requirementSavepoint = "before_completion_requirements";
            await stagingTransaction.CreateSavepointAsync(requirementSavepoint, cancellationToken).ConfigureAwait(false);
            var requirements = SupervisorUnitContract.BuildStakedRequirements(tasks
                .Where(t => !string.IsNullOrEmpty(t.Task.SubtaskId) && contractHashes.ContainsKey(t.Task.SubtaskId!))
                .Select(t => (t.Task.SubtaskId!, contractHashes[t.Task.SubtaskId!], acceptanceUnits?.Contains(t.Task.SubtaskId!) == true, deliveryUnits?.Contains(t.Task.SubtaskId!) == true)),
                Messages.Contracts.ContractAuthority.ModelProposal, planRef);

            if (requirements.Count > 0)
            {
                try
                {
                    var revisions = await _contracts.UpsertRequirementsAsync(context.SupervisorRunId, context.TeamId, requirements, cancellationToken).ConfigureAwait(false);

                    // P1 (v4.3, receipt↔revision binding): the attempt is dispatched UNDER the acceptance revision
                    // this very stake produced — or, on a crash-replayed staging, the identical one the idempotent
                    // upsert left standing — stamped BEFORE the agent rows are created so every receipt the attempt
                    // ever mints inherits the binding through its WorkUnitRef. A reclaimed orphan keeps its crashed
                    // pass's persisted stamp, which the replayed no-op stake resolves to the same revision.
                    tasks = tasks.Select(t => t.Task.WorkUnit is { } wu && t.Task.SubtaskId is { Length: > 0 } id && revisions.TryGetValue((SupervisorUnitContract.AcceptanceRef(id), Messages.Contracts.ContractKinds.Acceptance), out var revision)
                        ? (t.Task with { WorkUnit = wu with { RequirementRevision = revision } }, t.Spec)
                        : t).ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A PostgreSQL statement error aborts the transaction until it rolls back to a savepoint. Restore
                    // the wave transaction before honoring this store's historical fail-open requirement behavior.
                    await stagingTransaction.RollbackToSavepointAsync(requirementSavepoint, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogWarning(ex, "Persisting completion requirements failed for run {RunId}; staging proceeds — the composer will read an incomplete obligation set as Unknown", context.SupervisorRunId);
                }
            }
        }

        var agentRunIds = new List<Guid>(tasks.Count);
        var reclaimedAny = false;

        for (var k = 0; k < tasks.Count; k++)
        {
            // Reuse a reclaimed orphan for the leading slots (crash recovery — these were created by a prior
            // crashed pass of THIS decision, whose persisted TaskJson was ALREADY persona-resolved, so re-running
            // the resolver here would be redundant); else resolve the persona into the task (mirroring
            // WorkflowEngine.StageAgentRunAsync) then create the durable agent run (Queued) through the admission
            // gate — team inherited from the supervisor run, never model-supplied. Linked to the supervisor run
            // + node so the completion notifier resumes the right run, and the reconciler's parent-terminal
            // guard governs it.
            var reclaimed = k < orphans.Count;
            reclaimedAny |= reclaimed;

            var agentRunId = reclaimed
                ? orphans[k]
                : await CreateResolvedAgentRunAsync(tasks[k].Task, tasks[k].Spec, context, cancellationToken).ConfigureAwait(false);

            StageAgentWait(context, k, agentRunId);
            agentRunIds.Add(agentRunId);
        }

        // One SaveChanges for all K wait rows; prior per-agent flushes are still uncommitted in stagingTransaction.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await stagingTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // A crash-recovery reclaim reuses the orphan's OWN already-persisted TaskJson verbatim (never re-resolved,
        // see above) — so if THIS pass freshly recomputed an escalation pick, it can drift from what the CRASHED
        // pass actually baked into that orphan (the team's model pool may have changed between the crash and this
        // replay: a model disabled/enabled, re-tiered, or newly credentialed). Reconcile against the one AgentRun
        // this retry actually dispatches on, so the recorded "to" always describes truth, never a stale re-guess.
        // A NO-OP escalation (To null — nothing in the pool beat the prior tier) has no dispatched model to
        // reconcile against: stamping the orphan's own model there would turn "no stronger model existed" into a
        // claim that one was picked.
        if (escalation is { To: not null } && reclaimedAny)
            escalation = await ReconcileEscalationWithDispatchedModelAsync(escalation, agentRunIds[0], cancellationToken).ConfigureAwait(false);

        var outcome = JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Count, escalation }, AgentJson.Options);

        _logger.LogInformation("Supervisor staged {Count} agent run(s) at turn {Turn} on node {NodeId} (reused {Reused} crash orphan(s)); units: {Units}", agentRunIds.Count, context.TurnNumber, context.NodeId, Math.Min(orphans.Count, tasks.Count), DescribeStagedUnits(tasks));

        return SupervisorExecution.ParkedOnAgents(outcome, agentRunIds.Count);
    }

    /// <summary>The plan-local unit ids this staging dispatches, comma-joined ("s1,s2"); a task with no subtask key (a free-form spawn under no plan) reads "(unkeyed)". Pure + pinned — the other half of the plan log's edges↔units join.</summary>
    internal static string DescribeStagedUnits(IReadOnlyList<(AgentTask Task, SupervisorAgentDispatch? Spec)> tasks) =>
        string.Join(",", tasks.Select(t => string.IsNullOrEmpty(t.Task.SubtaskId) ? "(unkeyed)" : t.Task.SubtaskId));

    /// <summary>The crash-recovery correction for <see cref="StageAgentsAndParkAsync"/>: reads the reclaimed orphan's OWN persisted <see cref="AgentTask.Model"/> back off its TaskJson — never re-derived — and stamps it as the escalation's <c>To</c>, since that row's dispatch was fixed by the crashed pass, not by this replay. Falls back to the original guess only if the row/model can't be read (best-effort, never throws) — a slightly-stale note is still better than a hard failure over purely informational metadata.</summary>
    private async Task<SupervisorRetryEscalationOutcome> ReconcileEscalationWithDispatchedModelAsync(SupervisorRetryEscalationOutcome escalation, Guid dispatchedAgentRunId, CancellationToken cancellationToken)
    {
        var taskJson = await _db.AgentRun.AsNoTracking().Where(r => r.Id == dispatchedAgentRunId).Select(r => r.TaskJson).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(taskJson)) return escalation;

        var actualModel = Deserialize<AgentTask>(taskJson)?.Model;

        return NullIfBlank(actualModel) is { } model ? escalation with { To = model } : escalation;
    }

    /// <summary>This turn's already-staged AgentRun wait tokens (the agent-run ids) in spawn-index order, or empty when none — the recovery anchor for a crash AFTER the waits committed but before the terminal was recorded.</summary>
    private async Task<IReadOnlyList<Guid>> ExistingTurnWaitAgentIdsAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var keyPrefix = $"{context.NodeId}#turn{context.TurnNumber}#";

        var waits = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == context.SupervisorRunId && w.NodeId == context.NodeId
                        && w.WaitKind == WorkflowWaitKinds.AgentRun && w.IterationKey.StartsWith(keyPrefix))
            .Select(w => new { w.IterationKey, w.Token })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Order by the PARSED NUMERIC spawn index, NOT the lexicographic IterationKey: the key's trailing #{k} is raw
        // (non-zero-padded), so a text sort yields #0,#1,#10,…,#2 for K≥11 — scrambling agentRunIds out of the authored
        // subtaskIds[i] order the fan-out + the per-unit acceptance join rely on. SQL can't parse the index, so order
        // in memory (K ≤ 20).
        return waits
            .OrderBy(w => SupervisorOutcome.SpawnIndexOf(w.IterationKey))
            .Select(w => Guid.TryParse(w.Token, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }

    /// <summary>Re-park on the K waits a prior crashed pass already staged this turn — re-derive the outcome from their tokens WITHOUT staging or creating anything (no double-spawn). The node re-suspends on the existing waits.</summary>
    private SupervisorExecution ReparkOnExistingWaits(SupervisorTurnContext context, IReadOnlyList<Guid> agentRunIds)
    {
        _logger.LogInformation("Supervisor re-parking on {Count} agent wait(s) already staged at turn {Turn} on node {NodeId} (crash recovery — no re-spawn)", agentRunIds.Count, context.TurnNumber, context.NodeId);

        var outcome = JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Count }, AgentJson.Options);

        return SupervisorExecution.ParkedOnAgents(outcome, agentRunIds.Count);
    }

    /// <summary>
    /// The <c>Queued</c> agent runs linked to this supervisor run + node, in creation order — orphans a prior
    /// crashed pass of this spawn/retry decision staged before its waits committed. We only reach a spawn turn
    /// with zero pending AgentRun waits (the node re-parks otherwise), so a Queued agent here is always this
    /// turn's crash orphan to reuse — never a healthy in-flight agent (those are claimed past Queued).
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ReclaimableOrphanAgentIdsAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
        await _db.AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == context.SupervisorRunId && r.NodeId == context.NodeId && r.Status == AgentRunStatus.Queued)
            .OrderBy(r => r.CreatedDate).ThenBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Stage the k-th AgentRun wait under the per-turn-per-spawn IterationKey (must-fix #1). Token = the agent-run id (the completion notifier resolves the wait by it). Distinct row per (turn, k) → no collision, no clobber.</summary>
    private void StageAgentWait(SupervisorTurnContext context, int spawnIndex, Guid agentRunId)
    {
        _db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(),
            RunId = context.SupervisorRunId,
            NodeId = context.NodeId,
            IterationKey = SupervisorOutcome.AgentWaitKey(context.NodeId, context.TurnNumber, spawnIndex),
            WaitKind = WorkflowWaitKinds.AgentRun,
            Token = agentRunId.ToString(),
            Status = WorkflowWaitStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>Fold the most recent prior <c>plan</c> decision's subtasks into a lookup so spawn/retry can build each agent's instruction from the plan-local id. B3: each subtask's <c>Acceptance</c> is the EFFECTIVE spec through the co-sign overlay — the SAME chokepoint the fold grades by, so an approved amendment that ADDS a spec forces the push opt-in on (F4) for the very retry that will be graded against it, and a waived subtask stops forcing it.</summary>
    internal static IReadOnlyDictionary<string, SupervisorPlannedSubtask> ResolvePlannedSubtasks(SupervisorTurnContext context)
    {
        var lookup = new Dictionary<string, SupervisorPlannedSubtask>();

        var lastPlan = context.PriorDecisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Plan);

        if (lastPlan == null) return lookup;

        var plan = Deserialize<SupervisorPlanPayload>(lastPlan.PayloadJson);

        if (plan == null) return lookup;

        foreach (var subtask in plan.Subtasks)
            lookup[subtask.Id] = subtask;

        var effective = SupervisorAcceptanceOverlay.Resolve(context.PriorDecisions,
            lookup.Where(kv => kv.Value.Acceptance is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Acceptance!));

        foreach (var id in lookup.Keys.ToList())
        {
            var spec = effective.WaivedSubtaskIds.Contains(id) ? null : effective.BySubtask.GetValueOrDefault(id);

            if (!ReferenceEquals(lookup[id].Acceptance, spec)) lookup[id] = lookup[id] with { Acceptance = spec };
        }

        return lookup;
    }

    /// <summary>
    /// Resolve the spawned task's persona (if any) into it BEFORE persisting — mirroring
    /// <c>WorkflowEngine.StageAgentRunAsync</c> so a supervisor-supplied <c>AgentDefinitionId</c> actually MERGES
    /// (system-prompt prepended, persona model/tools/credential folded), not just persisted inert. The merged
    /// task is frozen into the run's TaskJson, so a crash-recovery reclaim (which reuses the already-created run)
    /// never re-resolves — the resolve is a deterministic pre-transform on a FRESH stage only.
    ///
    /// <para>A missing / foreign / corrupt persona is a CLEAN node failure, mirroring
    /// <c>WorkflowEngine.StageAgentRunAsync</c>'s <c>AgentDefinitionResolutionException</c> → node-failure
    /// translation: the message is prefixed for the supervisor lane and re-thrown as the SAME exception type so
    /// the turn service records a terminal failure (no stranded-Running decision) and the node fails cleanly
    /// (composing with node retry + the <c>error</c> branch) — not a misleading engine-bootstrap failure.</para>
    /// </summary>
    private async Task<Guid> CreateResolvedAgentRunAsync(AgentTask task, SupervisorAgentDispatch? spec, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        // P3 — resolve the model-authored per-agent persona SLUG to a team AgentDefinitionId and stamp it BEFORE the
        // persona MERGE below, so a per-agent persona actually embodies (its system prompt / model / tools fold in via
        // ResolveAsync). A FRESH-stage-only pre-transform (this method runs only for non-orphan slots), so a crash
        // reclaim that reuses the already-resolved TaskJson never re-resolves a renamed/deleted persona.
        task = await ApplyDispatchPersonaAsync(task, spec, context, cancellationToken).ConfigureAwait(false);

        AgentTask resolved;

        try
        {
            resolved = await _agentDefinitionResolver.ResolveAsync(task, context.TeamId, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentDefinitionResolutionException ex)
        {
            throw new AgentDefinitionResolutionException($"agent.supervisor spawn: {ex.Message}", ex);
        }

        // POST-RESOLUTION pool gate (option B): the EFFECTIVE model — model-authored, profile default, OR a persona-
        // filled one (resolved just above) — must be a credentialed model in the operator's allowed pool (empty pool =
        // ALL the team's credentialed models), and the agent runs on THAT row's credential. A null model is the harness
        // default (no name → no gate). Out of pool → fail-closed (terminalized by the turn service's catch), so the pool
        // is not bypassable via a persona reference or a profile default.
        resolved = await ApplyDispatchModelAsync(resolved, context, cancellationToken).ConfigureAwait(false);

        // POST-RESOLUTION persona pool gate (same single-point design as the model gate): the EFFECTIVE persona — a
        // model-authored slug (resolved to an id at ApplyDispatchPersonaAsync) OR the run-level profile default — must be
        // in the operator's allowed agent pool. Gating the RESOLVED id once covers both paths uniformly, so the pool is
        // not bypassable via a model-authored slug or the profile default. Empty pool = all team personas; no persona
        // (pure-inline) = no gate. Out of pool → fail-closed (terminalized by the turn service's catch).
        resolved = ApplyDispatchAgentPool(resolved, context);
        resolved = ApplyDispatchHarnessPool(resolved, spec, context);

        // Stamp the owning TURN cell (<nodeId>#turn{N}) so a supervisor's spawned agents are addressable by the
        // turn that spawned them (D4) — the turn-grain analogue of the per-spawn wait key <nodeId>#turn{N}#{k}.
        var turnCellKey = $"{context.NodeId}#turn{context.TurnNumber}";

        return (await _agentRuns.CreateAsync(resolved, context.TeamId, context.SupervisorRunId, context.NodeId, turnCellKey, cancellationToken).ConfigureAwait(false)).Id;
    }

    /// <summary>
    /// Resolve a model-authored per-agent persona SLUG (L4 — the third Auto axis) to a team-scoped
    /// <c>AgentDefinitionId</c> and stamp it, OVERRIDING the run-level profile persona <see cref="BuildTaskWithGoal"/>
    /// seeded — so each agent can embody a DISTINCT persona the brain picked from the catalog (its system prompt /
    /// model / tools then merge in the <c>ResolveAsync</c> step that follows). FAIL-CLOSED on an unknown / foreign /
    /// deleted slug — but this is NO LONGER the path a model-authored slug takes: ExecuteSpawnAsync now pre-resolves every
    /// authored slug and rejects the spawn re-authorably before staging, because reaching here terminalized the run. The
    /// throw stays as a defence-in-depth invariant guard for a caller that skipped that pre-flight, and it is NOT a clean
    /// terminal — it propagates as an unhandled node exception and fails the whole run. No slug → unchanged (the profile
    /// persona stands; byte-identical to a homogeneous spawn).
    /// </summary>
    private async Task<AgentTask> ApplyDispatchPersonaAsync(AgentTask task, SupervisorAgentDispatch? spec, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        if (NullIfBlank(spec?.AgentDefinition) is not { } slug) return task;

        var personaId = await _agentDefinitionResolver.ResolveSlugAsync(slug, context.TeamId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentDefinitionResolutionException($"agent.supervisor spawn requests persona '{slug}', which is not an active persona in this team's library.");

        return task with { AgentDefinitionId = personaId };
    }

    /// <summary>
    /// Resolve the spawned agent's effective model NAME to a credentialed pool row (option B): the model + the credential
    /// it runs on both come from that row, so a dispatched agent can only run a model the team credentialed — and on that
    /// model's own key. Bounded to the operator's <see cref="SupervisorTurnContext.AllowedModelIds"/> pool (empty = all
    /// the team's credentialed models). A null effective model with NO bound pool is the harness default (no name, no
    /// pool → no gate); a null effective model WITH a bound pool still resolves + gates against it
    /// (<see cref="ApplyPoolBoundDefaultAsync"/>) — a persona/profile that left the model unset must not let an
    /// operator's "Agent model pool" of one silently escape to <c>ModelCredentialResolver</c>'s unbounded full-team
    /// default at execution.
    /// <para>This is NO LONGER the path a model-AUTHORED name takes: <c>ExecuteSpawnAsync</c> screens every authored name
    /// before staging (<see cref="ScreenAuthoredModelsAsync"/>) and rejects an unresolvable one re-authorably, because
    /// reaching the throw here killed the run mid fan-out. What still arrives here is an effective model the MODEL did
    /// not author — one a resolved persona or the run profile filled in — plus any authored name as defence in depth.
    /// The throw is NOT a clean terminal: the turn service records THIS DECISION Failed (no stranded <c>Running</c> row)
    /// and then RE-THROWS, so the node fails and the run terminalizes Failure.</para>
    /// </summary>
    private async Task<AgentTask> ApplyDispatchModelAsync(AgentTask resolved, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        if (NullIfBlank(resolved.Model) is not { } effectiveModel) return await ApplyPoolBoundDefaultAsync(resolved, context, cancellationToken).ConfigureAwait(false);

        var dispatch = await _modelSelector.ResolveDispatchAsync(context.TeamId, effectiveModel, context.AllowedModelIds, cancellationToken).ConfigureAwait(false)
            ?? throw new SupervisorModelAccessException($"agent.supervisor spawn requests model '{effectiveModel}', which is not a credentialed model in this run's allowed model pool.");

        return WithDispatchedModel(resolved, dispatch, context);
    }

    /// <summary>
    /// No effective model name at all (an unfilled persona/profile default) but the operator bound an agent-model
    /// POOL: that pool must still constrain the dispatch, else the agent falls through to
    /// <c>ModelCredentialResolver.ResolveTeamDefaultAsync</c>'s FULL team pool at execution, silently ignoring a pool
    /// of one. Ranked with the SAME agent-plane precedence the full-team default uses, just bounded to this pool
    /// (<see cref="IModelPoolSelector.ResolvePoolDefaultAsync"/> — reused, not reinvented). Null/empty pool ⇒
    /// unchanged (the harness's own no-name default applies, byte-identical to before this fix).
    /// </summary>
    private async Task<AgentTask> ApplyPoolBoundDefaultAsync(AgentTask resolved, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        if (context.AllowedModelIds is not { Count: > 0 } pool) return resolved;

        var dispatch = await _modelSelector.ResolvePoolDefaultAsync(context.TeamId, pool, cancellationToken).ConfigureAwait(false)
            ?? throw new SupervisorModelAccessException("agent.supervisor spawn has no model name, and none of this run's allowed model pool resolves to an enabled, active-credential row.");

        return WithDispatchedModel(resolved, dispatch, context);
    }

    /// <summary>Authoring-time compatibility clamp (P1), shared by the named-model and pool-bound-default dispatch paths: the resolved model runs on a credential of THIS provider, so pin a harness that can drive it — the authored/default harness if it already can, else a registered one that does. The model (or the pool) decided the MODEL; the server makes the harness match it (the run-time reconciler is the backstop).</summary>
    private AgentTask WithDispatchedModel(AgentTask resolved, ModelDispatchRef dispatch, SupervisorTurnContext context)
    {
        var allowedHarnesses = AgentHarnessPool.Clamp(_harnesses.All, context.AllowedAgentKinds);
        var harness = HarnessModelReconciler.Reconcile(resolved.Harness, dispatch.Provider, allowedHarnesses, AgentHarnessDefaults.DefaultHarness).HarnessKind;

        return resolved with { Model = dispatch.ModelId, ModelCredentialId = dispatch.ModelCredentialId, Harness = harness };
    }

    /// <summary>
    /// The persona analogue of <see cref="ApplyDispatchModelAsync"/>: the spawned agent's EFFECTIVE persona id (a
    /// model-authored slug already resolved to an id, OR the run-level profile default) must be in the operator's
    /// <see cref="SupervisorTurnContext.AllowedAgentDefinitionIds"/> pool (empty = ALL the team's personas). A null id is
    /// a pure-inline run (no persona → no gate). Out of pool → <see cref="SupervisorAgentAccessException"/> (fail-closed,
    /// terminalized by the turn service's catch) — so the pool is not bypassable via a model-authored slug or a profile default.
    /// </summary>
    private static AgentTask ApplyDispatchAgentPool(AgentTask resolved, SupervisorTurnContext context)
    {
        if (resolved.AgentDefinitionId is not { } personaId) return resolved;

        if (context.AllowedAgentDefinitionIds is not { Count: > 0 } pool || pool.Contains(personaId)) return resolved;

        throw new SupervisorAgentAccessException($"agent.supervisor spawn requests persona '{personaId}', which is not in this run's allowed agent pool.");
    }

    /// <summary>
    /// Final effective-harness gate. It runs after model reconciliation because that step may legitimately switch the
    /// authored harness to one capable of driving the selected provider.
    /// <para>A MODEL-authored kind outside the pool THROWS — defence in depth behind <see cref="ScreenAuthoredHarnesses"/>,
    /// which raises the same refusal before anything stages. Any OTHER out-of-pool kind is one NOBODY authored against the
    /// allow-list: the run profile's harness, or the platform floor <see cref="AgentHarnessDefaults.DefaultHarness"/> that
    /// stands in when no profile named one. Those are CLAMPED into the pool rather than killing the run — the operator who
    /// allow-lists only <c>claude-code</c> and leaves the profile harness empty used to lose EVERY spawn of that run to the
    /// codex floor colliding with their own list, which is a config shape the node schema actively invites.</para>
    /// <para>The message names the EFFECTIVE harness, which on the throw branch is the authored one verbatim: the only
    /// step that can move a harness between authoring and here is <see cref="ApplyDispatchModelAsync"/>'s reconcile, and
    /// that already picks from the pool-clamped registry, so it cannot turn an admitted authored kind into an excluded one.</para>
    /// </summary>
    private static AgentTask ApplyDispatchHarnessPool(AgentTask resolved, SupervisorAgentDispatch? spec, SupervisorTurnContext context)
    {
        if (context.AllowedAgentKinds is not { Count: > 0 } pool || pool.Contains(resolved.Harness, StringComparer.OrdinalIgnoreCase)) return resolved;

        if (NullIfBlank(spec?.Harness) is not null)
            throw new SupervisorAgentAccessException($"agent.supervisor spawn requests harness '{resolved.Harness}', which is a registered adapter but the operator did not admit to this run's allowed harness pool.");

        return resolved with { Harness = AdmittedSubstitute(pool) };
    }

    /// <summary>The kind that stands in for an unauthored out-of-pool harness: the platform default when the operator admitted it, else the ordinal-first admitted kind — a pure, deterministic pick, so two agents of one spawn can never disagree about it.</summary>
    private static string AdmittedSubstitute(IReadOnlyList<string> pool) =>
        pool.FirstOrDefault(kind => string.Equals(kind, AgentHarnessDefaults.DefaultHarness, StringComparison.OrdinalIgnoreCase))
            ?? pool.OrderBy(kind => kind, StringComparer.Ordinal).First();

    /// <summary>
    /// Build the agent task for a subtask id. The GOAL folds the revised instruction (retry) wins, else the
    /// planned instruction, else the supervisor goal. Every other field is stamped from the run's optional
    /// <see cref="SupervisorTurnContext.AgentProfile"/> (P2-3), mirroring <c>AgentCodeNode</c>'s config→task map
    /// so a spawned agent is a REAL team agent (repo / harness / model / persona / credential / runner / MCP /
    /// autonomy + the supervisor's conversation as its approval surface).
    ///
    /// <para>BYTE-IDENTICAL when no profile: with <see cref="SupervisorTurnContext.AgentProfile"/> null/absent —
    /// what a pre-P2-3 supervisor resolves to — every field below evaluates to today's exact value (Harness =
    /// <c>codex-cli</c>, Autonomy = Standard, everything else null/default), so existing spawn/crash/bound/E2E
    /// tests stay green. The approval-conversation alone is threaded from <see cref="SupervisorTurnContext.ConversationId"/>
    /// (the supervisor's own conversation, null in the bare case) — a stored-only field that nothing reads on the
    /// spawn path, so it doesn't perturb behaviour.</para>
    /// </summary>
    internal static AgentTask BuildAgentTask(IReadOnlyDictionary<string, SupervisorPlannedSubtask> subtasks, string subtaskId, string? revisedInstruction, SupervisorTurnContext context, SupervisorAgentDispatch? spec = null, DependencyStagingResult? staging = null)
    {
        var planned = subtasks.GetValueOrDefault(subtaskId);

        var instruction = !string.IsNullOrWhiteSpace(revisedInstruction) ? revisedInstruction!
            : !string.IsNullOrWhiteSpace(planned?.Instruction) ? planned!.Instruction
            : context.Goal;

        // Stamp the subtask id (D1 retry-resume linking key) so a later RETRY of this subtask can find this attempt and
        // resume its conversation. Blank id → null (byte-identical; the generic BuildTaskWithGoal never sets it).
        // F4 on the SUPERVISOR lane (the AgentCodeNode rule, missing here until it killed a real run): a per-unit
        // contract implies a GRADABLE branch — the fold grades this subtask's oracle on its produced branch, so the
        // publish opt-in is forced ON. Without it a stock profile (push off) fails every contracted unit
        // "no-branch-or-repo" even when the work and the check are both perfect.
        return BuildTaskWithGoal(WithHandoff(staging?.GoalFoldText, WithRole(spec?.Role, instruction)), context, forcePushBranch: planned?.Acceptance is not null, spec: spec, primaryRef: staging?.Ref) with
        {
            SubtaskId = string.IsNullOrWhiteSpace(subtaskId) ? null : subtaskId,
            // The CLEAN instruction — before WithRole/WithHandoff prepend role framing or a producer/prior-attempt fold
            // block — so a subtask's card title reads as its own work, never a role prefix or handoff paragraph.
            DisplayTitle = instruction,
        };
    }

    /// <summary>Fold a model-authored role into the agent's GOAL so it runs in-role — the role's only sink (there is no <c>AgentTask.Role</c> field; it shapes the prompt, never a privilege). Blank role → the plain instruction (byte-identical to a no-dispatch spawn).</summary>
    private static string WithRole(string? role, string instruction) =>
        string.IsNullOrWhiteSpace(role) ? instruction : $"As the {role.Trim()}, {instruction}";

    /// <summary>Prepend the S1 handoff block (a producer's branch + summary + file count) to the instruction, so a dependent subtask's prompt names what it inherits. Null/blank fold text → the plain instruction (byte-identical to a subtask with no dependency).</summary>
    private static string WithHandoff(string? foldText, string instruction) =>
        string.IsNullOrWhiteSpace(foldText) ? instruction : $"{foldText}\n\n{instruction}";

    /// <summary>
    /// Build a spawned agent's task from a GOAL string + the run's profile — the shared field-stamping the spawn,
    /// retry, AND resolve (#379) paths reuse so a supervisor-spawned agent is always a REAL team agent (repo /
    /// harness / model / persona / credential / runner / MCP / autonomy + the supervisor's conversation as its
    /// approval surface), regardless of which verb spawned it. <paramref name="forcePushBranch"/> overrides the
    /// profile's push opt-in to TRUE — the resolver MUST push its reconciled branch (a downstream PR-open needs a
    /// head), and a CONTRACT-BEARING subtask must push so its acceptance can grade (F4). A contract-less spawn/retry
    /// passes false → byte-identical to before (the profile's <c>PushBranch</c> wins). <paramref name="primaryRef"/>
    /// (S1 handoff) overrides the clone ref to a dependency's own branch or a fresh integration branch — null (the
    /// default, and every non-dependent spawn) keeps the byte-identical repository-default-branch clone.
    /// </summary>
    internal static AgentTask BuildTaskWithGoal(string goal, SupervisorTurnContext context, bool forcePushBranch = false, SupervisorAgentDispatch? spec = null, string? primaryRef = null)
    {
        var profile = context.AgentProfile;
        var boundRelated = AgentWorkspaceAuthoring.ParseRelatedRepositories(profile?.RelatedRepositories ?? default);

        // L4 arc B — the model PROPOSES this agent's repos + autonomy on the per-agent dispatch; the server CLAMPS.
        // The primary + related subset must be within the operator's bound repos (B2, throws on an out-of-set repo or
        // a read→write escalation), and autonomy can only LOWER past the profile ceiling. With no dispatch (spec null)
        // every clamp collapses to the profile value → byte-identical to a pre-L4 homogeneous spawn.
        var repositoryId = SupervisorRepoClamp.ClampPrimary(spec?.RepositoryId, profile?.RepositoryId, boundRelated);
        var related = spec?.TargetRepos is { } targetRepos
            ? SupervisorRepoClamp.IntersectWithBoundRepos(targetRepos, profile?.RepositoryId, boundRelated)
            : boundRelated;

        // A per-agent repo authoring (a primary override, or a TargetRepos subset) may name a repo that is ALSO the
        // resolved primary — drop the primary from the related set so it is cloned ONCE (as the writable primary), never
        // into two mounts. Only when the model authored repos, so a role-only / no-dispatch spawn keeps the profile's
        // repos verbatim (byte-identical).
        if (spec?.RepositoryId is not null || spec?.TargetRepos is not null)
            related = related.Where(r => r.RepositoryId != repositoryId).ToList();

        var autonomy = ClampAutonomy(spec?.AutonomyLevel, AutonomyOf(profile));

        // S1: a dependency-staging handoff ref outranks everything (continuing work rides the prior branch — neither
        // the operator's BaseBranch nor the pin can express that). Otherwise the PROFILE primary clones the operator's
        // launch-pinned branch at the launch pin; a dispatch that PROMOTED a bound related repo to primary carries
        // THAT repo's own baked pin (resolved for it at launch — its homogeneous siblings mount the same commit),
        // and no branch ref (the operator's BaseBranch names the profile primary's branch, not the override's).
        var primaryBaseRef = primaryRef ?? (repositoryId == profile?.RepositoryId ? NullIfBlank(profile?.BaseRef) : null);
        var primaryPin = primaryRef is not null ? null
            : repositoryId == profile?.RepositoryId ? NullIfBlank(profile?.PinnedSha)
            : NullIfBlank(boundRelated.FirstOrDefault(r => r.RepositoryId == repositoryId)?.PinnedSha);

        return new AgentTask
        {
            Goal = goal,
            Harness = NullIfBlank(spec?.Harness) ?? HarnessOf(profile),
            // The operator's harness allow-list rides ALONG to execution, where the run-time reconciler may swap the
            // harness for one that can drive the model's provider — that swap picks from the registry clamped by this
            // list, so the run cannot end up on a kind the operator never admitted. Null pool → null → unbounded.
            AllowedHarnessKinds = context.AllowedAgentKinds is { Count: > 0 } kinds ? kinds : null,
            // Stamp the RAW authored model name (L4 dispatch wins over the profile default). The pool gate runs
            // POST-resolution in CreateResolvedAgentRunAsync — where the EFFECTIVE model (incl. a persona-filled one) is
            // known — so this stays a pure projection. A null name → the harness default (no pool gate; no name).
            Model = NullIfBlank(spec?.Model) ?? NullIfBlank(profile?.Model),
            AgentDefinitionId = profile?.AgentDefinitionId,
            ModelCredentialId = profile?.ModelCredentialId,
            Tools = context.SpawnedAgentTools,
            RunnerKind = NullIfBlank(profile?.RunnerKind),
            RepositoryId = repositoryId,
            // The authored related repos project onto a Workspace via the SHARED authoring底層 the agent.run node uses —
            // no related repos → null → byte-identical single-repo spawn (RepositoryId drives it). The operator's
            // multi-repo cwd mode rides the profile (null/Auto → byte-identical). The primary's ref + pin resolve
            // above (handoff > profile branch/pin > promoted related repo's own pin).
            Workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(repositoryId, related, cwdMode: WorkspaceCwdModeWire.FromWire(profile?.CwdMode) ?? WorkspaceCwdMode.Auto, primaryRef: primaryBaseRef, primaryPinnedSha: primaryPin),
            Autonomy = autonomy,
            Permissions = AgentAutonomyPolicy.Derive(autonomy),
            // The profile's wall-clock cap, in the agent.run node's vocabulary: positive caps the run, an explicit ≤0
            // means NO wall-clock (the operator's "no timeout" choice), absent → the bounded 1h default — so the Launch
            // timeout override finally reaches the agents that do the hours of work.
            TimeoutSeconds = profile?.TimeoutSeconds is { } timeout ? (timeout > 0 ? timeout : (int?)null) : 3600,
            ApprovalConversationId = context.ConversationId,
            EnableMcpEndpoint = profile?.EnableMcp,
            PushProducedBranch = forcePushBranch ? true : profile?.PushBranch,
            OutputReviewMode = profile?.OutputReviewMode ?? ReviewMode.None,
            ReviewerModelId = profile?.ReviewerModelId,
            // Explicit 0, not null: null would let Improve imply an in-run revise round (the executor's default),
            // STACKING with the supervisor's own retry loop — the supervisor is the revision mechanism for its units
            // (it sees the flagged/failed unit and retries with a revised instruction), so units never self-revise.
            MaxReviseRounds = 0,
        };
    }

    /// <summary>The profile's harness, else the shared platform default (<see cref="AgentHarnessDefaults.DefaultHarness"/> — the same operator-overridable, codex-cli-floor source the agent.run projection uses). Null/blank profile → byte-identical to pre-P2-3.</summary>
    private static string HarnessOf(SupervisorAgentProfile? profile) =>
        !string.IsNullOrWhiteSpace(profile?.Harness) ? profile!.Harness! : AgentHarnessDefaults.DefaultHarness;

    /// <summary>The profile's autonomy tier parsed case-insensitively, else the safe <see cref="AgentAutonomyLevel.Standard"/> default (mirrors agent.run's ReadAutonomyLevel). Null/unrecognised → byte-identical to pre-P2-3.</summary>
    private static AgentAutonomyLevel AutonomyOf(SupervisorAgentProfile? profile) =>
        Enum.TryParse<AgentAutonomyLevel>(profile?.AutonomyLevel, ignoreCase: true, out var level) ? level : AgentAutonomyLevel.Standard;

    /// <summary>Clamp a model-authored autonomy REQUEST to the run profile's <paramref name="ceiling"/> (L4 arc B): the request wins only when it is MORE restrictive than the ceiling (the enum is ordered Confined &lt; Standard &lt; Trusted &lt; Unleashed); an absent / unparseable / equal-or-higher request keeps the ceiling — so the model can lower its own autonomy but NEVER raise it past the operator's grant. No request → the ceiling (byte-identical).</summary>
    private static AgentAutonomyLevel ClampAutonomy(string? requested, AgentAutonomyLevel ceiling) =>
        Enum.TryParse<AgentAutonomyLevel>(requested, ignoreCase: true, out var level) && level < ceiling ? level : ceiling;

    /// <summary>A blank string degrades to null (the harness-default sentinel), mirroring agent.run's ReadOptionalString.</summary>
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
