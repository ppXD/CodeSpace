using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// The SYNCHRONOUS merge half of the real executor (Rule 10 <c>.Merge.cs</c>): read the recorded prior-Attempt
/// agent results by id + fold them into one outcome. Each merged entry carries the FULL <see cref="AgentRunResult"/>
/// work products — <c>summary</c> AND <c>changedFiles</c> / <c>producedBranch</c> / <c>patch</c> / <c>error</c> — so
/// the synthesis never discards what each agent produced (the branch + diff a downstream PR-open step consumes). A
/// large diff that was offloaded to the artifact store (D2: PatchArtifactId set, inline Patch cleared by terminal
/// persistence) is RESOLVED back here, so the merge never silently loses a big agent's work product.
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

        var contributors = await ReadMergeContributorsAsync(agentRunIds, context.TeamId, cancellationToken).ConfigureAwait(false);
        var merged = contributors.Agents;

        // The deterministic fold — byte-identical to pre-SOTA-#3: an ordered dictionary whose first three keys
        // serialize exactly as the old anonymous { merged, count, synthesisInstruction }. The optional integration +
        // synthesis keys are layered ONLY when the gate is on (RealSupervisorActionExecutor.Integrate.cs).
        var outcome = new Dictionary<string, object?>
        {
            ["merged"] = merged.Select(ProjectMergedEntry).ToList(),
            ["count"] = merged.Count,
            ["synthesisInstruction"] = merge.SynthesisInstruction,
        };

        if (contributors.Integrity is not null) outcome["contributorIntegrity"] = contributors.Integrity;

        await AugmentWithIntegrationAndSynthesisAsync(outcome, context, contributors, merge, cancellationToken).ConfigureAwait(false);

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
    /// Collect the agent-run ids recorded by EVERY spawn/retry decision in the active Plan generation (in order) — the
    /// merge folds that generation's Attempt results — MINUS any unit a per-unit acceptance grade objectively REJECTED (loopability slice 4,
    /// "局部綠≠整合綠"): a unit that failed its OWN definition-of-done must NOT be integrated into the reviewable head,
    /// even if the model merges. The verdict (<see cref="SupervisorAgentResult.AcceptancePassed"/>) rides each spawn
    /// outcome's <c>agentResults</c> by agent-run id; a unit re-RUN after a rejection has a fresh id, so its retry
    /// (passing or ungraded) integrates while the rejected original is withheld. A unit with NO verdict (ungraded — no
    /// per-unit contract, the pre-slice case) integrates exactly as before (byte-identical). A plan-less legacy tape
    /// retains the old whole-tape fold.
    /// </summary>
    internal static IReadOnlyList<Guid> ResolveAgentRunIdsToMerge(SupervisorTurnContext context)
    {
        var staging = SupervisorPlanWindow.Read(context.PriorDecisions).Decisions
            .Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)
            .ToList();

        var rejected = staging
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .Where(SupervisorOutcome.IsWithheldFromHead)
            .Select(r => r.AgentRunId)
            .ToHashSet();

        return staging
            .SelectMany(d => SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson))
            .Where(id => !rejected.Contains(id))
            .ToList();
    }

    /// <summary>
    /// Load every recorded active-generation contributor id without dropping holes. The one query reads cross-team
    /// rows only as identity + tenant scope (their error/result columns are SQL-CASEd to null), then materializes
    /// trustworthy same-team terminal results in spawn order. Missing/cross-team/non-terminal/malformed/contradictory
    /// ids become a bounded <see cref="SupervisorMergeContributorIntegrity"/> fact; database failures still throw as
    /// infrastructure failures, and required artifact reads retain their existing typed exception.
    /// </summary>
    private async Task<MergeContributorRead> ReadMergeContributorsAsync(IReadOnlyList<Guid> agentRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        if (agentRunIds.Count == 0) return new MergeContributorRead(Array.Empty<MergedAgent>(), null);

        var loaded = await _db.AgentRun.AsNoTracking()
            .Where(r => agentRunIds.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                r.TeamId,
                r.Status,
                Error = r.TeamId == teamId ? r.Error : null,
                ResultJson = r.TeamId == teamId ? r.ResultJson : null,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byId = loaded.ToDictionary(r => r.Id, r => new MergeContributorRow(r.Id, r.TeamId, r.Status, r.Error, r.ResultJson));
        var merged = new List<MergedAgent>(agentRunIds.Count);
        var issues = new List<SupervisorMergeContributorIssue>();
        var artifacts = new MergeRequiredArtifactMemo(_offloader, teamId);

        foreach (var agentRunId in agentRunIds)
        {
            if (!byId.TryGetValue(agentRunId, out var row))
            {
                issues.Add(Issue(agentRunId, SupervisorMergeContributorIssueKind.MissingRow));
                continue;
            }

            if (row.TeamId != teamId)
            {
                issues.Add(Issue(agentRunId, SupervisorMergeContributorIssueKind.CrossTeam));
                continue;
            }

            if (ReadIntegrityIssue(row, out var result) is { } kind)
            {
                issues.Add(Issue(agentRunId, kind));
                continue;
            }

            merged.Add(await ProjectMergedAgentAsync(row, result, artifacts, cancellationToken).ConfigureAwait(false));
        }

        var integrity = issues.Count == 0
            ? null
            : new SupervisorMergeContributorIntegrity { ExpectedCount = agentRunIds.Count, MaterializedCount = merged.Count, Issues = issues };

        return new MergeContributorRead(merged, integrity);
    }

    private static SupervisorMergeContributorIssue Issue(Guid agentRunId, SupervisorMergeContributorIssueKind kind) => new() { AgentRunId = agentRunId, Kind = kind };

    /// <summary>Pure row/result consistency check. Failure kinds are stable enums, never exception strings or model-capability labels.</summary>
    private static SupervisorMergeContributorIssueKind? ReadIntegrityIssue(MergeContributorRow row, out AgentRunResult? result)
    {
        result = null;

        if (!AgentRunStateMachine.IsTerminal(row.Status)) return SupervisorMergeContributorIssueKind.NonTerminalRow;

        if (string.IsNullOrWhiteSpace(row.ResultJson))
            return row.Status is Messages.Enums.AgentRunStatus.Succeeded or Messages.Enums.AgentRunStatus.NeedsReview
                ? SupervisorMergeContributorIssueKind.MissingRequiredResult
                : null;

        try { result = JsonSerializer.Deserialize<AgentRunResult>(row.ResultJson, AgentJson.Options); }
        catch (JsonException) { return SupervisorMergeContributorIssueKind.MalformedResult; }

        if (result is null) return SupervisorMergeContributorIssueKind.MalformedResult;
        return result.Status != row.Status ? SupervisorMergeContributorIssueKind.ResultStatusMismatch : null;
    }

    /// <summary>Project ONE validated agent run into the typed <see cref="MergedAgent"/> — the compact fields from the SHARED <see cref="SupervisorOutcome.ProjectCompact"/> PLUS the offloaded-aware patch + recorded base SHA. Validation happens before any artifact fetch, while a required artifact failure still propagates unchanged.</summary>
    private async Task<MergedAgent> ProjectMergedAgentAsync(MergeContributorRow row, AgentRunResult? result, MergeRequiredArtifactMemo artifacts, CancellationToken cancellationToken)
    {
        var compact = SupervisorOutcome.ProjectCompact(row.Id, row.Status.ToString(), row.Error, row.ResultJson);

        var patch = await ResolvePatchAsync(result, row.Id, artifacts, cancellationToken).ConfigureAwait(false);

        var repositoryResults = await ResolveRepositoryPatchesAsync(result, row.Id, artifacts, cancellationToken).ConfigureAwait(false);

        return new MergedAgent
        {
            AgentRunId = row.Id,
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
    private static async Task<IReadOnlyList<RepositoryRunResult>> ResolveRepositoryPatchesAsync(AgentRunResult? result, Guid agentRunId, MergeRequiredArtifactMemo artifacts, CancellationToken cancellationToken)
    {
        if (result is null || result.RepositoryResults.Count == 0) return Array.Empty<RepositoryRunResult>();

        var resolved = new List<RepositoryRunResult>(result.RepositoryResults.Count);

        foreach (var repo in result.RepositoryResults)
        {
            var patch = await artifacts.ResolveRequiredAsync(agentRunId, repo.Patch, repo.PatchArtifactId, cancellationToken).ConfigureAwait(false);

            resolved.Add(repo with { Patch = patch, PatchArtifactId = null });
        }

        return resolved;
    }

    /// <summary>Resolve a terminal persisted patch carrier. <c>AgentRunService</c> clears the bounded executor-side compatibility copy before storing a non-null D2 PatchArtifactId, so these inputs are mutually exclusive here: inline when small, otherwise the full referenced diff. Empty when there's neither. Routes through the shared <see cref="IArtifactOffloader"/> — the same primitive the producer used.</summary>
    private static Task<string> ResolvePatchAsync(AgentRunResult? result, Guid agentRunId, MergeRequiredArtifactMemo artifacts, CancellationToken cancellationToken) =>
        result == null
            ? Task.FromResult("")
            : artifacts.ResolveRequiredAsync(agentRunId, result.Patch, result.PatchArtifactId, cancellationToken);

    /// <summary>
    /// Success-only memo for ONE <see cref="ReadMergeContributorsAsync"/> call. It memoizes only the immutable bytes
    /// behind an exact artifact id; repository alias selection remains the caller's job and every first encounter still
    /// goes through the existing team-scoped, fail-closed required reader. AgentRunId is part of the key so two
    /// producers independently prove the artifact they name. A failure/cancellation never inserts, and this object is
    /// method-local, so neither can escape into a later merge request.
    /// </summary>
    private sealed class MergeRequiredArtifactMemo
    {
        private readonly IArtifactOffloader _offloader;
        private readonly Guid _teamId;
        private readonly Dictionary<MergeRequiredArtifactKey, string> _resolved = new();

        public MergeRequiredArtifactMemo(IArtifactOffloader offloader, Guid teamId)
        {
            _offloader = offloader;
            _teamId = teamId;
        }

        public async Task<string> ResolveRequiredAsync(Guid agentRunId, string? inline, Guid? artifactId, CancellationToken cancellationToken)
        {
            // Preserve the required reader's exact carrier precedence. In particular, a malformed dual carrier still
            // returns its inline value and a legacy/no-carrier result still returns empty without entering the memo.
            if (!string.IsNullOrEmpty(inline) || artifactId is not { } id)
                return await _offloader.ResolveRequiredAsync(_teamId, inline, artifactId, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var key = new MergeRequiredArtifactKey(_teamId, agentRunId, id);
            if (_resolved.TryGetValue(key, out var resolved)) return resolved;

            resolved = await _offloader.ResolveRequiredAsync(_teamId, inline, id, cancellationToken).ConfigureAwait(false);
            _resolved.Add(key, resolved);
            return resolved;
        }
    }

    private readonly record struct MergeRequiredArtifactKey(Guid TeamId, Guid AgentRunId, Guid ArtifactId);

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

    private sealed record MergeContributorRead(IReadOnlyList<MergedAgent> Agents, SupervisorMergeContributorIntegrity? Integrity);

    private sealed record MergeContributorRow(Guid Id, Guid TeamId, Messages.Enums.AgentRunStatus Status, string? Error, string? ResultJson);
}
