using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// The SYNCHRONOUS merge half of the real executor (Rule 10 <c>.Merge.cs</c>): read the recorded prior-Attempt
/// agent results by id + fold them into one outcome. Each merged entry carries the FULL <see cref="AgentRunResult"/>
/// work products — <c>summary</c> AND <c>changedFiles</c> / <c>producedBranch</c> / <c>patch</c> / <c>error</c> — so
/// the synthesis never discards what each agent produced (the branch + diff a downstream PR-open step consumes). A
/// large diff that was offloaded to the artifact store (D2: PatchArtifactId set, inline Patch empty) is RESOLVED back
/// here, so the merge never silently loses a big agent's work product.
///
/// <para>SOTA #3: when the integrate gate is on (<c>RealSupervisorActionExecutor.Integrate.cs</c>) the fold is
/// AUGMENTED with an <c>integration</c> key (the K diffs INTEGRATED on disk into one reviewable branch, fail-safe)
/// and a <c>synthesis</c> key (a model reduce over the K REAL diffs). With the gate OFF the outcome is byte-identical
/// to pre-SOTA-#3: exactly <c>{ merged, count, synthesisInstruction }</c> — no clone, no LLM call.</para>
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    private async Task<SupervisorExecution> ExecuteMergeAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var merge = Deserialize<SupervisorMergePayload>(decision.PayloadJson) ?? new SupervisorMergePayload();

        var agentRunIds = ResolveAgentRunIdsToMerge(context);

        var merged = await ReadMergedAgentsAsync(agentRunIds, context.TeamId, cancellationToken).ConfigureAwait(false);

        // The deterministic fold — byte-identical to pre-SOTA-#3: an ordered dictionary whose first three keys
        // serialize exactly as the old anonymous { merged, count, synthesisInstruction }. The optional integration +
        // synthesis keys are layered ONLY when the gate is on (RealSupervisorActionExecutor.Integrate.cs).
        var outcome = new Dictionary<string, object?>
        {
            ["merged"] = merged.Select(ProjectMergedEntry).ToList(),
            ["count"] = merged.Count,
            ["synthesisInstruction"] = merge.SynthesisInstruction,
        };

        await AugmentWithIntegrationAndSynthesisAsync(outcome, context, merged, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Supervisor merged {Count} prior agent result(s)", merged.Count);

        return SupervisorExecution.Synchronous(JsonSerializer.Serialize(outcome, AgentJson.Options));
    }

    /// <summary>The byte-identical-to-today merged-array entry: the 8 work-product fields, NO baseSha (baseSha stays internal to <see cref="MergedAgent"/> for the integrate step, so the gate-OFF outcome is unchanged).</summary>
    private static object ProjectMergedEntry(MergedAgent a) => new
    {
        agentRunId = a.AgentRunId,
        status = a.Status,
        summary = a.Summary,
        changedFiles = a.ChangedFiles,
        producedBranch = a.ProducedBranch,
        patch = a.Patch,
        patchArtifactId = a.PatchArtifactId,
        error = a.Error,
    };

    /// <summary>
    /// Collect the agent-run ids recorded by EVERY prior spawn/retry decision (in order) — the merge folds all prior
    /// Attempt results — MINUS any unit a per-unit acceptance grade objectively REJECTED (loopability slice 4,
    /// "局部綠≠整合綠"): a unit that failed its OWN definition-of-done must NOT be integrated into the reviewable head,
    /// even if the model merges. The verdict (<see cref="SupervisorAgentResult.AcceptancePassed"/>) rides each spawn
    /// outcome's <c>agentResults</c> by agent-run id; a unit re-RUN after a rejection has a fresh id, so its retry
    /// (passing or ungraded) integrates while the rejected original is withheld. A unit with NO verdict (ungraded — no
    /// per-unit contract, the pre-slice case) integrates exactly as before (byte-identical).
    /// </summary>
    internal static IReadOnlyList<Guid> ResolveAgentRunIdsToMerge(SupervisorTurnContext context)
    {
        var staging = context.PriorDecisions
            .Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)
            .ToList();

        var rejected = staging
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .Where(IsAcceptanceRejected)
            .Select(r => r.AgentRunId)
            .ToHashSet();

        return staging
            .SelectMany(d => SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson))
            .Where(id => !rejected.Contains(id))
            .ToList();
    }

    /// <summary>
    /// The SINGLE definition of "this unit's work is WITHHELD from the reviewable head" (loopability slice 4): its
    /// per-unit acceptance grade objectively REJECTED it (<see cref="SupervisorAgentResult.AcceptancePassed"/> == false).
    /// Shared by BOTH doors to the head — the merge (<see cref="ResolveAgentRunIdsToMerge"/>) AND the resolver's
    /// branch collection (<c>RealSupervisorActionExecutor.Resolve.cs</c>) — so the two can never drift on which units a
    /// rejection withholds. An ungraded unit (<c>null</c> — no per-unit contract, or a deferred multi-repo unit) is NOT
    /// withheld (byte-identical to pre-slice).
    /// </summary>
    internal static bool IsAcceptanceRejected(SupervisorAgentResult result) => result.AcceptancePassed == false;

    /// <summary>Load each agent run's FULL terminal result by id, TEAM-SCOPED (defense-in-depth — the ids are this run's own recorded spawns, but a cross-team id never resolves) into the typed <see cref="MergedAgent"/> the merged-array projection AND the integrate step both consume (one read). A missing / non-terminal run contributes its status, not a crash. Preserves spawn order.</summary>
    private async Task<IReadOnlyList<MergedAgent>> ReadMergedAgentsAsync(IReadOnlyList<Guid> agentRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        if (agentRunIds.Count == 0) return Array.Empty<MergedAgent>();

        var runs = await _db.AgentRun.AsNoTracking()
            .Where(r => agentRunIds.Contains(r.Id) && r.TeamId == teamId)
            .Select(r => new { r.Id, r.Status, r.Error, r.ResultJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byId = runs.ToDictionary(r => r.Id);

        var ordered = agentRunIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();

        var merged = new List<MergedAgent>(ordered.Count);
        foreach (var r in ordered)
            merged.Add(await ProjectMergedAgentAsync(r.Id, r.Status, r.Error, r.ResultJson, teamId, cancellationToken).ConfigureAwait(false));

        return merged;
    }

    /// <summary>Project ONE agent run into the typed <see cref="MergedAgent"/> — the compact fields from the SHARED <see cref="SupervisorOutcome.ProjectCompact"/> (one source of truth the decider-visibility fold also uses, so they can't drift; it folds the ROW error for a cancelled/abandoned agent whose result is null) PLUS the resolved (offloaded-aware) patch + the recorded base SHA (the integrate anchor). A missing / unparseable result still contributes its status.</summary>
    private async Task<MergedAgent> ProjectMergedAgentAsync(Guid agentRunId, Messages.Enums.AgentRunStatus status, string? rowError, string? resultJson, Guid teamId, CancellationToken cancellationToken)
    {
        var compact = SupervisorOutcome.ProjectCompact(agentRunId, status.ToString(), rowError, resultJson);

        var result = string.IsNullOrWhiteSpace(resultJson) ? null : Deserialize<AgentRunResult>(resultJson);

        var patch = await ResolvePatchAsync(result, teamId, cancellationToken).ConfigureAwait(false);

        var repositoryResults = await ResolveRepositoryPatchesAsync(result, teamId, cancellationToken).ConfigureAwait(false);

        return new MergedAgent
        {
            AgentRunId = compact.AgentRunId,
            Status = compact.Status,
            Summary = compact.Summary,
            ChangedFiles = compact.ChangedFiles,
            ProducedBranch = compact.ProducedBranch,
            Patch = patch,
            PatchArtifactId = result?.PatchArtifactId,
            Error = compact.Error,
            BaseSha = result?.BaseSha,
            RepositoryResults = repositoryResults,
        };
    }

    /// <summary>
    /// Resolve each writable repo's per-repo diff (offloaded ones fetched back, team-scoped) so the multi-repo per-repo
    /// integrate has every repo's inline patch in hand — the per-repo analogue of <see cref="ResolvePatchAsync"/>. EMPTY
    /// for a single-repo run (no <see cref="AgentRunResult.RepositoryResults"/>), so the single-repo integrate path is
    /// untouched. The artifact id is cleared once resolved (the inline <c>Patch</c> now carries the full diff).
    /// </summary>
    private async Task<IReadOnlyList<RepositoryRunResult>> ResolveRepositoryPatchesAsync(AgentRunResult? result, Guid teamId, CancellationToken cancellationToken)
    {
        if (result is null || result.RepositoryResults.Count == 0) return Array.Empty<RepositoryRunResult>();

        var resolved = new List<RepositoryRunResult>(result.RepositoryResults.Count);

        foreach (var repo in result.RepositoryResults)
        {
            var patch = await _offloader.ResolveAsync(teamId, repo.Patch, repo.PatchArtifactId, cancellationToken).ConfigureAwait(false);

            resolved.Add(repo with { Patch = patch, PatchArtifactId = null });
        }

        return resolved;
    }

    /// <summary>The inline patch when present; otherwise the full diff resolved from the artifact store via the D2 PatchArtifactId ref (so an offloaded large diff is folded into the merge, not lost). Empty when there's neither. Routes through the shared <see cref="IArtifactOffloader"/> — the same primitive the producer used.</summary>
    private Task<string> ResolvePatchAsync(AgentRunResult? result, Guid teamId, CancellationToken cancellationToken) =>
        result == null
            ? Task.FromResult("")
            : _offloader.ResolveAsync(teamId, result.Patch, result.PatchArtifactId, cancellationToken);

    /// <summary>One merged agent's full work products — the typed holder the merged-array projection AND the SOTA #3 integrate step both read (so the gate-OFF array stays byte-identical while the integrate step gets baseSha + the resolved patch). Internal scratch, not a persisted noun.</summary>
    private sealed class MergedAgent
    {
        public required Guid AgentRunId { get; init; }
        public required string Status { get; init; }
        public string? Summary { get; init; }
        public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();
        public string? ProducedBranch { get; init; }
        public string Patch { get; init; } = "";
        public Guid? PatchArtifactId { get; init; }
        public string? Error { get; init; }
        public string? BaseSha { get; init; }

        /// <summary>This agent's PER-REPO work products (multi-repo run), each with its diff RESOLVED (offloaded fetched back) — what the per-repo integrate (<c>.Integrate.cs</c>) feeds the integrator one repo at a time. Empty for a single-repo agent (its one outcome is the top-level <see cref="Patch"/>/<see cref="BaseSha"/>/<see cref="ProducedBranch"/>).</summary>
        public IReadOnlyList<RepositoryRunResult> RepositoryResults { get; init; } = Array.Empty<RepositoryRunResult>();
    }
}
