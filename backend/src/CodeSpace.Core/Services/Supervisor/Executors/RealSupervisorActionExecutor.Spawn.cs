using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses;
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
/// <c>agent.code</c> child runs + K <c>AgentRun</c> waits, then the node parks on them (the wait-for-all
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

        // Fan out over the subtask ids (already clamped to the dependency-ready frontier when the decision was formed —
        // see SupervisorTurnService.ClampSpawnToDependencyFrontier — so the persisted payload's subtaskIds match the
        // staged agents one-for-one). For each, apply the model-authored per-agent dispatch override (L4 arc B) when the
        // spawn carries one keyed by that subtask id, else build a homogeneous profile clone (byte-identical to before).
        // The dispatch spec rides ALONGSIDE the task so the async stage can resolve its per-agent persona slug (P3) on a
        // FRESH stage only — a crash-recovery orphan reclaim reuses the already-resolved task and never re-resolves.
        var tasks = spawn.SubtaskIds
            .Select(id => { var spec = DispatchFor(spawn, id); return (BuildAgentTask(subtasks, id, spec?.GoalOverride, context, spec), spec); })
            .ToList();

        return await StageAgentsAndParkAsync(tasks, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The model-authored per-agent dispatch for a subtask id (the FIRST matching <c>agents[]</c> entry — lenient on duplicates), or null. A spawn with no <c>agents[]</c> returns null for every id → byte-identical homogeneous fan-out.</summary>
    private static SupervisorAgentDispatch? DispatchFor(SupervisorSpawnPayload spawn, string subtaskId) =>
        spawn.Agents?.FirstOrDefault(a => a.SubtaskId == subtaskId);

    /// <summary>Retry: re-run ONE prior subtask as a FRESH agent run (a new Attempt), optionally with a revised instruction. Same stage-K-waits + barrier as spawn (here K = 1).</summary>
    private async Task<SupervisorExecution> ExecuteRetryAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var retry = Deserialize<SupervisorRetryPayload>(decision.PayloadJson);
        var subtasks = ResolvePlannedSubtasks(context);

        var tasks = retry == null || string.IsNullOrWhiteSpace(retry.SubtaskId)
            ? new List<(AgentTask, SupervisorAgentDispatch?)>()
            : new List<(AgentTask, SupervisorAgentDispatch?)> { (BuildAgentTask(subtasks, retry.SubtaskId, retry.RevisedInstruction, context), null) };

        return await StageAgentsAndParkAsync(tasks, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Create each agent run (through the admission gate, team-inherited) + stage its AgentRun wait keyed
    /// <c>&lt;nodeId&gt;#turn{N}#{k}</c>, then record the agent-run ids + count in the outcome. The node parks
    /// on the K waits; <see cref="WorkflowEngine"/>'s post-Suspended-commit <c>DispatchPendingAgentRunAsync</c>
    /// dispatches them, and the barrier resumes the supervisor once all complete. An empty task list (a no-op
    /// spawn / a retry with no subtask) records a zero-agent SYNCHRONOUS outcome so the node self-advances
    /// rather than parking forever on nothing.
    ///
    /// <para>IDEMPOTENT under crash recovery (the must-fix): the agent rows are committed one-at-a-time (each
    /// <c>CreateAsync</c> saves) while the waits flush together at the end, so a crash mid fan-out leaves the
    /// decision stuck Running with one of two partial states. The turn service re-executes that decision under
    /// its existing Running claim; this method makes the re-execution land EXACTLY K agents + K waits with no
    /// double-spawn and no leaked orphan:
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
    private async Task<SupervisorExecution> StageAgentsAndParkAsync(IReadOnlyList<(AgentTask Task, SupervisorAgentDispatch? Spec)> tasks, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        if (tasks.Count == 0)
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(new { agentRunIds = Array.Empty<Guid>(), agentCount = 0, note = "no subtasks to spawn" }, AgentJson.Options));

        var existingWaitAgentIds = await ExistingTurnWaitAgentIdsAsync(context, cancellationToken).ConfigureAwait(false);

        if (existingWaitAgentIds.Count > 0)
            return ReparkOnExistingWaits(context, existingWaitAgentIds);

        var orphans = await ReclaimableOrphanAgentIdsAsync(context, cancellationToken).ConfigureAwait(false);

        var agentRunIds = new List<Guid>(tasks.Count);

        for (var k = 0; k < tasks.Count; k++)
        {
            // Reuse a reclaimed orphan for the leading slots (crash recovery — these were created by a prior
            // crashed pass of THIS decision, whose persisted TaskJson was ALREADY persona-resolved, so re-running
            // the resolver here would be redundant); else resolve the persona into the task (mirroring
            // WorkflowEngine.StageAgentRunAsync) then create the durable agent run (Queued) through the admission
            // gate — team inherited from the supervisor run, never model-supplied. Linked to the supervisor run
            // + node so the completion notifier resumes the right run, and the reconciler's parent-terminal
            // guard governs it.
            var agentRunId = k < orphans.Count
                ? orphans[k]
                : await CreateResolvedAgentRunAsync(tasks[k].Task, tasks[k].Spec, context, cancellationToken).ConfigureAwait(false);

            StageAgentWait(context, k, agentRunId);
            agentRunIds.Add(agentRunId);
        }

        // One SaveChanges for all K wait rows — the agent rows are already persisted (CreateAsync saves each).
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var outcome = JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Count }, AgentJson.Options);

        _logger.LogInformation("Supervisor staged {Count} agent run(s) at turn {Turn} on node {NodeId} (reused {Reused} crash orphan(s))", agentRunIds.Count, context.TurnNumber, context.NodeId, Math.Min(orphans.Count, tasks.Count));

        return SupervisorExecution.ParkedOnAgents(outcome, agentRunIds.Count);
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

    /// <summary>Fold the most recent prior <c>plan</c> decision's subtasks into a lookup so spawn/retry can build each agent's instruction from the plan-local id.</summary>
    private static IReadOnlyDictionary<string, SupervisorPlannedSubtask> ResolvePlannedSubtasks(SupervisorTurnContext context)
    {
        var lookup = new Dictionary<string, SupervisorPlannedSubtask>();

        var lastPlan = context.PriorDecisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Plan);

        if (lastPlan == null) return lookup;

        var plan = Deserialize<SupervisorPlanPayload>(lastPlan.PayloadJson);

        if (plan == null) return lookup;

        foreach (var subtask in plan.Subtasks)
            lookup[subtask.Id] = subtask;

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

        // Stamp the owning TURN cell (<nodeId>#turn{N}) so a supervisor's spawned agents are addressable by the
        // turn that spawned them (D4) — the turn-grain analogue of the per-spawn wait key <nodeId>#turn{N}#{k}.
        var turnCellKey = $"{context.NodeId}#turn{context.TurnNumber}";

        return (await _agentRuns.CreateAsync(resolved, context.TeamId, context.SupervisorRunId, context.NodeId, turnCellKey, cancellationToken).ConfigureAwait(false)).Id;
    }

    /// <summary>
    /// Resolve the spawned agent's effective model NAME to a credentialed pool row (option B): the model + the credential
    /// it runs on both come from that row, so a dispatched agent can only run a model the team credentialed — and on that
    /// model's own key. Bounded to the operator's <see cref="SupervisorTurnContext.AllowedModelIds"/> pool (empty = all
    /// the team's credentialed models). A null effective model is the harness default (no name → no gate). Out of pool →
    /// <see cref="SupervisorModelAccessException"/> (fail-closed, terminalized by the turn service's catch).
    /// </summary>
    /// <summary>
    /// Resolve a model-authored per-agent persona SLUG (L4 — the third Auto axis) to a team-scoped
    /// <c>AgentDefinitionId</c> and stamp it, OVERRIDING the run-level profile persona <see cref="BuildTaskWithGoal"/>
    /// seeded — so each agent can embody a DISTINCT persona the brain picked from the catalog (its system prompt /
    /// model / tools then merge in the <c>ResolveAsync</c> step that follows). FAIL-CLOSED on an unknown / foreign /
    /// deleted slug (a clean terminal, mirroring <see cref="ApplyDispatchModelAsync"/>'s out-of-pool throw) — the brain
    /// only authors slugs the catalog lists. No slug → unchanged (the profile persona stands; byte-identical to a
    /// homogeneous spawn).
    /// </summary>
    private async Task<AgentTask> ApplyDispatchPersonaAsync(AgentTask task, SupervisorAgentDispatch? spec, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        if (NullIfBlank(spec?.AgentDefinition) is not { } slug) return task;

        var personaId = await _agentDefinitionResolver.ResolveSlugAsync(slug, context.TeamId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentDefinitionResolutionException($"agent.supervisor spawn requests persona '{slug}', which is not an active persona in this team's library.");

        return task with { AgentDefinitionId = personaId };
    }

    private async Task<AgentTask> ApplyDispatchModelAsync(AgentTask resolved, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        if (NullIfBlank(resolved.Model) is not { } effectiveModel) return resolved;

        var dispatch = await _modelSelector.ResolveDispatchAsync(context.TeamId, effectiveModel, context.AllowedModelIds, cancellationToken).ConfigureAwait(false)
            ?? throw new SupervisorModelAccessException($"agent.supervisor spawn requests model '{effectiveModel}', which is not a credentialed model in this run's allowed model pool.");

        // Authoring-time compatibility clamp (P1): the resolved model runs on a credential of THIS provider, so pin a
        // harness that can drive it — the authored/default harness if it already can, else a registered one that does.
        // The model authored the MODEL; the server makes the harness match it (the run-time reconciler is the backstop).
        var harness = HarnessModelReconciler.Reconcile(resolved.Harness, dispatch.Provider, _harnesses.All, AgentHarnessDefaults.DefaultHarness).HarnessKind;

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
    internal static AgentTask BuildAgentTask(IReadOnlyDictionary<string, SupervisorPlannedSubtask> subtasks, string subtaskId, string? revisedInstruction, SupervisorTurnContext context, SupervisorAgentDispatch? spec = null)
    {
        var planned = subtasks.GetValueOrDefault(subtaskId);

        var instruction = !string.IsNullOrWhiteSpace(revisedInstruction) ? revisedInstruction!
            : !string.IsNullOrWhiteSpace(planned?.Instruction) ? planned!.Instruction
            : context.Goal;

        return BuildTaskWithGoal(WithRole(spec?.Role, instruction), context, spec: spec);
    }

    /// <summary>Fold a model-authored role into the agent's GOAL so it runs in-role — the role's only sink (there is no <c>AgentTask.Role</c> field; it shapes the prompt, never a privilege). Blank role → the plain instruction (byte-identical to a no-dispatch spawn).</summary>
    private static string WithRole(string? role, string instruction) =>
        string.IsNullOrWhiteSpace(role) ? instruction : $"As the {role.Trim()}, {instruction}";

    /// <summary>
    /// Build a spawned agent's task from a GOAL string + the run's profile — the shared field-stamping the spawn,
    /// retry, AND resolve (#379) paths reuse so a supervisor-spawned agent is always a REAL team agent (repo /
    /// harness / model / persona / credential / runner / MCP / autonomy + the supervisor's conversation as its
    /// approval surface), regardless of which verb spawned it. <paramref name="forcePushBranch"/> overrides the
    /// profile's push opt-in to TRUE (the resolver MUST push its reconciled branch so a downstream PR-open has a
    /// head); spawn/retry pass false → byte-identical to before (the profile's <c>PushBranch</c> wins).
    /// </summary>
    internal static AgentTask BuildTaskWithGoal(string goal, SupervisorTurnContext context, bool forcePushBranch = false, SupervisorAgentDispatch? spec = null)
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

        return new AgentTask
        {
            Goal = goal,
            Harness = NullIfBlank(spec?.Harness) ?? HarnessOf(profile),
            // Stamp the RAW authored model name (L4 dispatch wins over the profile default). The pool gate runs
            // POST-resolution in CreateResolvedAgentRunAsync — where the EFFECTIVE model (incl. a persona-filled one) is
            // known — so this stays a pure projection. A null name → the harness default (no pool gate; no name).
            Model = NullIfBlank(spec?.Model) ?? NullIfBlank(profile?.Model),
            AgentDefinitionId = profile?.AgentDefinitionId,
            ModelCredentialId = profile?.ModelCredentialId,
            Tools = context.SpawnedAgentTools,
            RunnerKind = NullIfBlank(profile?.RunnerKind),
            RepositoryId = repositoryId,
            // The authored related repos project onto a Workspace via the SHARED authoring底層 the agent.code node uses —
            // no related repos → null → byte-identical single-repo spawn (RepositoryId drives it). The operator's
            // multi-repo cwd mode rides the profile (null/Auto → byte-identical).
            Workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(repositoryId, related, cwdMode: WorkspaceCwdModeWire.FromWire(profile?.CwdMode) ?? WorkspaceCwdMode.Auto),
            Autonomy = autonomy,
            Permissions = AgentAutonomyPolicy.Derive(autonomy),
            ApprovalConversationId = context.ConversationId,
            EnableMcpEndpoint = profile?.EnableMcp,
            PushProducedBranch = forcePushBranch ? true : profile?.PushBranch,
            OutputReviewMode = profile?.OutputReviewMode ?? ReviewMode.None,
            ReviewerModelId = profile?.ReviewerModelId,
        };
    }

    /// <summary>The profile's harness, else the shared platform default (<see cref="AgentHarnessDefaults.DefaultHarness"/> — the same operator-overridable, codex-cli-floor source the agent.code projection uses). Null/blank profile → byte-identical to pre-P2-3.</summary>
    private static string HarnessOf(SupervisorAgentProfile? profile) =>
        !string.IsNullOrWhiteSpace(profile?.Harness) ? profile!.Harness! : AgentHarnessDefaults.DefaultHarness;

    /// <summary>The profile's autonomy tier parsed case-insensitively, else the safe <see cref="AgentAutonomyLevel.Standard"/> default (mirrors agent.code's ReadAutonomyLevel). Null/unrecognised → byte-identical to pre-P2-3.</summary>
    private static AgentAutonomyLevel AutonomyOf(SupervisorAgentProfile? profile) =>
        Enum.TryParse<AgentAutonomyLevel>(profile?.AutonomyLevel, ignoreCase: true, out var level) ? level : AgentAutonomyLevel.Standard;

    /// <summary>Clamp a model-authored autonomy REQUEST to the run profile's <paramref name="ceiling"/> (L4 arc B): the request wins only when it is MORE restrictive than the ceiling (the enum is ordered Confined &lt; Standard &lt; Trusted &lt; Unleashed); an absent / unparseable / equal-or-higher request keeps the ceiling — so the model can lower its own autonomy but NEVER raise it past the operator's grant. No request → the ceiling (byte-identical).</summary>
    private static AgentAutonomyLevel ClampAutonomy(string? requested, AgentAutonomyLevel ceiling) =>
        Enum.TryParse<AgentAutonomyLevel>(requested, ignoreCase: true, out var level) && level < ceiling ? level : ceiling;

    /// <summary>A blank string degrades to null (the harness-default sentinel), mirroring agent.code's ReadOptionalString.</summary>
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
