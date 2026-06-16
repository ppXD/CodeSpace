using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// The SYNCHRONOUS merge half of the real executor (Rule 10 <c>.Merge.cs</c>): read the recorded prior-Attempt
/// agent results by id + fold them into one synthesis outcome. Each merged entry now carries the FULL
/// <see cref="AgentRunResult"/> work products — <c>summary</c> AND <c>changedFiles</c> / <c>producedBranch</c> /
/// <c>patch</c> / <c>error</c> — so the synthesis no longer discards what each agent actually produced (the
/// branch + diff a downstream PR-open step consumes). The patch is already length-capped by the executor's
/// <c>TruncatePatch</c>, so it passes through as-is. The decision self-advances after recording (SYNCHRONOUS).
///
/// <para>DEFERRED (not this slice): (1) folding the per-branch patches into ONE branch via a real git
/// rebase / conflict-resolve — today each agent's branch + diff is carried side-by-side, not merged on disk;
/// (2) a second LLM-synthesis pass over the folded contributions (the deep merge stays a deterministic fold,
/// no model call). Both return with the richer LLM-synthesis merge slice.</para>
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

    /// <summary>Load each agent run's FULL terminal result by id, TEAM-SCOPED (defense-in-depth — the ids are this run's own recorded spawns, but a cross-team id never resolves) — every merged entry projects the complete work products the synthesis records. A missing / non-terminal run contributes its status, not a crash.</summary>
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
            .Select(r => ProjectContribution(r.Id, r.Status, r.ResultJson))
            .ToList();
    }

    /// <summary>Project ONE agent run's full contribution into the merge outcome — the real work products (summary + changedFiles + producedBranch + patch + error), not just the summary. A missing / unparseable result still contributes its status. The shape round-trips through <c>AgentJson.Options</c>.</summary>
    private static object ProjectContribution(Guid agentRunId, Messages.Enums.AgentRunStatus status, string? resultJson)
    {
        var result = string.IsNullOrWhiteSpace(resultJson) ? null : Deserialize<AgentRunResult>(resultJson);

        return new
        {
            agentRunId,
            status = status.ToString(),
            summary = result?.Summary,
            changedFiles = result?.ChangedFiles ?? Array.Empty<string>(),
            producedBranch = result?.ProducedBranch,
            patch = result?.Patch ?? "",
            error = result?.Error,
        };
    }
}
