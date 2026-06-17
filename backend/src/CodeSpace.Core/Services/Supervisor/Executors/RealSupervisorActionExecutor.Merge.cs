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
/// branch + diff a downstream PR-open step consumes). A large diff that was offloaded to the artifact store
/// (D2: PatchArtifactId set, inline Patch empty) is RESOLVED back here, so the merge never silently loses a
/// big agent's work product. The decision self-advances after recording (now ASYNC — it resolves artifacts).
///
/// <para>DEFERRED (not this slice): (1) folding the per-branch patches into ONE branch via a real git
/// rebase / conflict-resolve — today each agent's branch + diff is carried side-by-side, not merged on disk;
/// (2) a second LLM-synthesis pass over the folded contributions (the deep merge stays a deterministic fold,
/// no model call). Both return with the richer LLM-synthesis merge slice.</para>
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    private async Task<SupervisorExecution> ExecuteMergeAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var merge = Deserialize<SupervisorMergePayload>(decision.PayloadJson) ?? new SupervisorMergePayload();

        var agentRunIds = ResolveAgentRunIdsToMerge(context);

        var results = await ReadAgentResultsAsync(agentRunIds, context.TeamId, cancellationToken).ConfigureAwait(false);

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
    private async Task<IReadOnlyList<object>> ReadAgentResultsAsync(IReadOnlyList<Guid> agentRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        if (agentRunIds.Count == 0) return Array.Empty<object>();

        var runs = await _db.AgentRun.AsNoTracking()
            .Where(r => agentRunIds.Contains(r.Id) && r.TeamId == teamId)
            .Select(r => new { r.Id, r.Status, r.Error, r.ResultJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Preserve the spawn order (the query may return rows in any order).
        var byId = runs.ToDictionary(r => r.Id);

        var ordered = agentRunIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();

        var contributions = new List<object>(ordered.Count);
        foreach (var r in ordered)
            contributions.Add(await ProjectContributionAsync(r.Id, r.Status, r.Error, r.ResultJson, teamId, cancellationToken).ConfigureAwait(false));

        return contributions;
    }

    /// <summary>Project ONE agent run's full contribution into the merge outcome — the real work products (summary + changedFiles + producedBranch + patch + error), not just the summary. The compact fields come from the SHARED <see cref="SupervisorOutcome.ProjectCompact"/> (the one source of truth the decider-visibility fold also uses, so they can't drift), which folds the ROW error for a cancelled/abandoned agent whose result is null. The unbounded patch layers on top: when the diff was offloaded (D2: PatchArtifactId set, Patch empty) the full diff is RESOLVED back from the artifact store so the merge synthesis never silently loses a large agent's work product. A missing / unparseable result still contributes its status. The shape round-trips through <c>AgentJson.Options</c>.</summary>
    private async Task<object> ProjectContributionAsync(Guid agentRunId, Messages.Enums.AgentRunStatus status, string? rowError, string? resultJson, Guid teamId, CancellationToken cancellationToken)
    {
        var compact = SupervisorOutcome.ProjectCompact(agentRunId, status.ToString(), rowError, resultJson);

        var result = string.IsNullOrWhiteSpace(resultJson) ? null : Deserialize<AgentRunResult>(resultJson);

        var patch = await ResolvePatchAsync(result, teamId, cancellationToken).ConfigureAwait(false);

        return new
        {
            agentRunId = compact.AgentRunId,
            status = compact.Status,
            summary = compact.Summary,
            changedFiles = compact.ChangedFiles,
            producedBranch = compact.ProducedBranch,
            patch,
            patchArtifactId = result?.PatchArtifactId,
            error = compact.Error,
        };
    }

    /// <summary>The inline patch when present; otherwise the full diff resolved from the artifact store via the D2 PatchArtifactId ref (so an offloaded large diff is folded into the merge, not lost). Empty when there's neither. Routes through the shared <see cref="IArtifactOffloader"/> — the same primitive the producer used.</summary>
    private Task<string> ResolvePatchAsync(AgentRunResult? result, Guid teamId, CancellationToken cancellationToken) =>
        result == null
            ? Task.FromResult("")
            : _offloader.ResolveAsync(teamId, result.Patch, result.PatchArtifactId, cancellationToken);
}
