using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>One agent's model-authored allocation — its semantic role + the planned subtask title it was assigned. Either may be null (a homogeneous spawn or a flat plan yields both null).</summary>
public sealed record AgentAllocation(string? Role, string? SubtaskTitle);

/// <summary>
/// Pure per-agent folds over a run's supervisor decision tape, keyed by agent-run id — the model-authored ALLOCATION
/// (semantic role + the planned subtask title each agent was assigned) and the ledger-folded COMPACT result
/// (model / tokens / git-truth changed files). Shared by the phase board + room card (<c>SupervisorPhaseSource</c>) and
/// the Session Journal card (<c>AgentCardFactsSource</c>) so the two projections read the SAME ground truth and can
/// never drift — the reason a journal card once fell back to the raw instruction and lost its file count while the room
/// card showed the short subtask name + the git-truth files.
/// </summary>
public static class SupervisorAgentAllocation
{
    /// <summary>
    /// Joins the latest plan's subtasks (<c>subtaskId → title</c>) and each spawn's per-agent dispatch roles
    /// (<c>subtaskId → role</c>) onto every staged agent through the SAME <c>subtaskIds[i] ↔ agentRunIds[i]</c> staging
    /// order — so a spawn fans the title+role onto exactly the agent that ran it. A retry re-runs ONE subtask as a
    /// fresh agent (carrying the subtask title; no per-agent role). Pure + best-effort — a homogeneous spawn (no
    /// <c>agents[]</c>) or a flat plan simply yields null role/title.
    /// </summary>
    public static IReadOnlyDictionary<Guid, AgentAllocation> Map(IReadOnlyList<SupervisorDecisionRecord> decisions)
    {
        var plan = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Plan);
        var titleBySubtask = SupervisorOutcome.ReadPlanSubtasks(plan?.PayloadJson)
            .GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.Last().Title);

        var map = new Dictionary<Guid, AgentAllocation>();

        foreach (var d in decisions.Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry).OrderBy(d => d.Sequence))
        {
            var agentIds = SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson);

            if (d.DecisionKind == SupervisorDecisionKinds.Spawn)
            {
                var subtaskIds = SupervisorOutcome.ReadSpawnSubtaskIds(d.PayloadJson);
                var rolesBySubtask = SupervisorOutcome.ReadSpawnAgentRoles(d.PayloadJson);

                for (var i = 0; i < Math.Min(subtaskIds.Count, agentIds.Count); i++)
                    map[agentIds[i]] = new AgentAllocation(rolesBySubtask.GetValueOrDefault(subtaskIds[i]), titleBySubtask.GetValueOrDefault(subtaskIds[i]));
            }
            else if (SupervisorOutcome.ReadRetrySubtaskId(d.PayloadJson) is { } retried && agentIds.Count > 0)
                map[agentIds[0]] = new AgentAllocation(Role: null, SubtaskTitle: titleBySubtask.GetValueOrDefault(retried));
        }

        return map;
    }

    /// <summary>Every spawned agent's compact result (model + realized tokens + git-truth changed files), keyed by agent-run id — read straight off the staging decisions' folded <c>agentResults</c> (no DB, replay-deterministic). An id is unique to one decision; Last() is a defensive de-dup.</summary>
    public static IReadOnlyDictionary<Guid, SupervisorAgentResult> ResultsById(IReadOnlyList<SupervisorDecisionRecord> decisions) =>
        decisions
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .GroupBy(r => r.AgentRunId)
            .ToDictionary(g => g.Key, g => g.Last());
}
