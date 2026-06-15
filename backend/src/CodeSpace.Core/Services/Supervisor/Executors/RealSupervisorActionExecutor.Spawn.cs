using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
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
            .Select(id => BuildAgentTask(subtasks, id, revisedInstruction: null, context.Goal))
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
            : new List<AgentTask> { BuildAgentTask(subtasks, retry.SubtaskId, retry.RevisedInstruction, context.Goal) };

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
            // crashed pass of THIS decision); else create the durable agent run (Queued) through the admission
            // gate — team inherited from the supervisor run, never model-supplied. Linked to the supervisor run
            // + node so the completion notifier resumes the right run, and the reconciler's parent-terminal
            // guard governs it.
            var agentRunId = k < orphans.Count
                ? orphans[k]
                : (await _agentRuns.CreateAsync(tasks[k], context.TeamId, context.SupervisorRunId, context.NodeId, cancellationToken).ConfigureAwait(false)).Id;

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

    /// <summary>Build the agent task for a subtask id: the revised instruction (retry) wins, else the planned instruction, else the goal as a fallback. Standard autonomy (workspace-write, no network) — the safe default.</summary>
    private static AgentTask BuildAgentTask(IReadOnlyDictionary<string, SupervisorPlannedSubtask> subtasks, string subtaskId, string? revisedInstruction, string goal)
    {
        var planned = subtasks.GetValueOrDefault(subtaskId);

        var instruction = !string.IsNullOrWhiteSpace(revisedInstruction) ? revisedInstruction!
            : !string.IsNullOrWhiteSpace(planned?.Instruction) ? planned!.Instruction
            : goal;

        return new AgentTask
        {
            Goal = instruction,
            Harness = DefaultHarness,
            Autonomy = AgentAutonomyLevel.Standard,
        };
    }

    /// <summary>The default harness for a supervisor-spawned agent run (matches the agent.code node's catalog default). A per-decision harness override is a later concern.</summary>
    private const string DefaultHarness = "codex-cli";
}
