using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
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

        var tasks = spawn.SubtaskIds
            .Select(id => BuildAgentTask(subtasks, id, revisedInstruction: null, context))
            .ToList();

        return await StageAgentsAndParkAsync(tasks, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retry: re-run ONE prior subtask as a FRESH agent run (a new Attempt), optionally with a revised instruction. Same stage-K-waits + barrier as spawn (here K = 1).</summary>
    private async Task<SupervisorExecution> ExecuteRetryAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var retry = Deserialize<SupervisorRetryPayload>(decision.PayloadJson);
        var subtasks = ResolvePlannedSubtasks(context);

        var tasks = retry == null || string.IsNullOrWhiteSpace(retry.SubtaskId)
            ? new List<AgentTask>()
            : new List<AgentTask> { BuildAgentTask(subtasks, retry.SubtaskId, retry.RevisedInstruction, context) };

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
    private async Task<SupervisorExecution> StageAgentsAndParkAsync(IReadOnlyList<AgentTask> tasks, SupervisorTurnContext context, CancellationToken cancellationToken)
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
                : await CreateResolvedAgentRunAsync(tasks[k], context, cancellationToken).ConfigureAwait(false);

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

        var tokens = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == context.SupervisorRunId && w.NodeId == context.NodeId
                        && w.WaitKind == WorkflowWaitKinds.AgentRun && w.IterationKey.StartsWith(keyPrefix))
            .OrderBy(w => w.IterationKey)
            .Select(w => w.Token)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return tokens.Select(t => Guid.TryParse(t, out var id) ? id : (Guid?)null).Where(id => id.HasValue).Select(id => id!.Value).ToList();
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
    private async Task<Guid> CreateResolvedAgentRunAsync(AgentTask task, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        AgentTask resolved;

        try
        {
            resolved = await _agentDefinitionResolver.ResolveAsync(task, context.TeamId, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentDefinitionResolutionException ex)
        {
            throw new AgentDefinitionResolutionException($"agent.supervisor spawn: {ex.Message}", ex);
        }

        // Stamp the owning TURN cell (<nodeId>#turn{N}) so a supervisor's spawned agents are addressable by the
        // turn that spawned them (D4) — the turn-grain analogue of the per-spawn wait key <nodeId>#turn{N}#{k}.
        var turnCellKey = $"{context.NodeId}#turn{context.TurnNumber}";

        return (await _agentRuns.CreateAsync(resolved, context.TeamId, context.SupervisorRunId, context.NodeId, turnCellKey, cancellationToken).ConfigureAwait(false)).Id;
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
    internal static AgentTask BuildAgentTask(IReadOnlyDictionary<string, SupervisorPlannedSubtask> subtasks, string subtaskId, string? revisedInstruction, SupervisorTurnContext context)
    {
        var planned = subtasks.GetValueOrDefault(subtaskId);

        var instruction = !string.IsNullOrWhiteSpace(revisedInstruction) ? revisedInstruction!
            : !string.IsNullOrWhiteSpace(planned?.Instruction) ? planned!.Instruction
            : context.Goal;

        return BuildTaskWithGoal(instruction, context);
    }

    /// <summary>
    /// Build a spawned agent's task from a GOAL string + the run's profile — the shared field-stamping the spawn,
    /// retry, AND resolve (#379) paths reuse so a supervisor-spawned agent is always a REAL team agent (repo /
    /// harness / model / persona / credential / runner / MCP / autonomy + the supervisor's conversation as its
    /// approval surface), regardless of which verb spawned it. <paramref name="forcePushBranch"/> overrides the
    /// profile's push opt-in to TRUE (the resolver MUST push its reconciled branch so a downstream PR-open has a
    /// head); spawn/retry pass false → byte-identical to before (the profile's <c>PushBranch</c> wins).
    /// </summary>
    internal static AgentTask BuildTaskWithGoal(string goal, SupervisorTurnContext context, bool forcePushBranch = false)
    {
        var profile = context.AgentProfile;
        var autonomy = AutonomyOf(profile);

        return new AgentTask
        {
            Goal = goal,
            Harness = HarnessOf(profile),
            Model = NullIfBlank(profile?.Model),
            AgentDefinitionId = profile?.AgentDefinitionId,
            ModelCredentialId = profile?.ModelCredentialId,
            Tools = context.SpawnedAgentTools,
            RunnerKind = NullIfBlank(profile?.RunnerKind),
            RepositoryId = profile?.RepositoryId,
            // Multi-repo (S7): the authored related repos project onto a Workspace via the SHARED authoring底層 the
            // agent.code node uses — no related repos → null → byte-identical single-repo spawn (RepositoryId drives it).
            Workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(profile?.RepositoryId, AgentWorkspaceAuthoring.ParseRelatedRepositories(profile?.RelatedRepositories ?? default)),
            Autonomy = autonomy,
            Permissions = AgentAutonomyPolicy.Derive(autonomy),
            ApprovalConversationId = context.ConversationId,
            EnableMcpEndpoint = profile?.EnableMcp,
            PushProducedBranch = forcePushBranch ? true : profile?.PushBranch,
        };
    }

    /// <summary>The profile's harness, else the supervisor's <c>codex-cli</c> default (matches agent.code's catalog default). Null/blank profile → byte-identical to pre-P2-3.</summary>
    private static string HarnessOf(SupervisorAgentProfile? profile) =>
        !string.IsNullOrWhiteSpace(profile?.Harness) ? profile!.Harness! : DefaultHarness;

    /// <summary>The profile's autonomy tier parsed case-insensitively, else the safe <see cref="AgentAutonomyLevel.Standard"/> default (mirrors agent.code's ReadAutonomyLevel). Null/unrecognised → byte-identical to pre-P2-3.</summary>
    private static AgentAutonomyLevel AutonomyOf(SupervisorAgentProfile? profile) =>
        Enum.TryParse<AgentAutonomyLevel>(profile?.AutonomyLevel, ignoreCase: true, out var level) ? level : AgentAutonomyLevel.Standard;

    /// <summary>A blank string degrades to null (the harness-default sentinel), mirroring agent.code's ReadOptionalString.</summary>
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The default harness for a supervisor-spawned agent run (matches the agent.code node's catalog default).</summary>
    private const string DefaultHarness = "codex-cli";
}
