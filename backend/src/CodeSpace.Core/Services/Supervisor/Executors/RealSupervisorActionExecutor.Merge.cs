using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// The SYNCHRONOUS merge half of the real executor (Rule 10 <c>.Merge.cs</c>): read the recorded prior-Attempt
/// agent results by id + reduce them into one synthesis outcome. A THIN array fold (the minimal E3 synthesis —
/// collect each spawned agent's summary by subtask), NOT a second LLM call; a richer LLM synthesis is a later
/// slice. The decision self-advances after recording (SYNCHRONOUS).
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    private SupervisorExecution ExecuteMerge(SupervisorDecision decision, SupervisorTurnContext context)
    {
        var merge = Deserialize<SupervisorMergePayload>(decision.PayloadJson) ?? new SupervisorMergePayload();

        var agentRunIds = ResolveAgentRunIdsToMerge(context);

        var results = ReadAgentResults(agentRunIds, context.TeamId);

        var outcome = JsonSerializer.Serialize(new
        {
            merged = results,
            count = results.Count,
            synthesisInstruction = merge.SynthesisInstruction,
        }, AgentJson.Options);

        _logger.LogInformation("Supervisor merged {Count} prior agent result(s)", results.Count);

        return SupervisorExecution.Synchronous(outcome);
    }

    /// <summary>Collect the agent-run ids recorded by EVERY prior spawn/retry decision (in order) — the merge folds all prior Attempt results.</summary>
    private static IReadOnlyList<Guid> ResolveAgentRunIdsToMerge(SupervisorTurnContext context) =>
        context.PriorDecisions
            .Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)
            .SelectMany(d => SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson))
            .ToList();

    /// <summary>Load each agent run's terminal summary by id, TEAM-SCOPED (defense-in-depth — the ids are this run's own recorded spawns, but a cross-team id never resolves) — the reduced view the synthesis records. A missing / non-terminal run contributes its status, not a crash.</summary>
    private IReadOnlyList<object> ReadAgentResults(IReadOnlyList<Guid> agentRunIds, Guid teamId)
    {
        if (agentRunIds.Count == 0) return Array.Empty<object>();

        var runs = _db.AgentRun.AsNoTracking()
            .Where(r => agentRunIds.Contains(r.Id) && r.TeamId == teamId)
            .Select(r => new { r.Id, r.Status, r.ResultJson })
            .ToList();

        // Preserve the spawn order (the query may return rows in any order).
        var byId = runs.ToDictionary(r => r.Id);

        return agentRunIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Select(r => (object)new { agentRunId = r.Id, status = r.Status.ToString(), summary = ReadSummary(r.ResultJson) })
            .ToList();
    }

    private static string? ReadSummary(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;

        var result = Deserialize<AgentRunResult>(resultJson);

        return result?.Summary;
    }
}
