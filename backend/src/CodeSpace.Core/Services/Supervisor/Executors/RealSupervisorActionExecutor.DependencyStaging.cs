using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// S1 handoff (Rule 10 <c>.DependencyStaging.cs</c>): resolve a dependent subtask's spawn-time workspace ref +
/// goal fold from its producers' recorded <see cref="Persistence.Entities.PublishManifest"/> rows — the single
/// source of truth (PR-1) — NEVER the repository's default branch as a silent fallback (the root cause of run
/// 28fec923: a dependent's fresh clone of the default branch never saw its producer's work).
///
/// <list type="bullet">
///   <item>0 real producers (no declared dependency, or every dependency made no changes to this repo) →
///         <see cref="DependencyStagingResult.NoOverride"/> — byte-identical to today's default-branch clone.</item>
///   <item>Exactly 1 producer with a pushed branch → that branch, verbatim (the cheap, common case).</item>
///   <item>Otherwise (≥2 producers, or the lone producer is patch-only) → reuse the SAME <see cref="IBranchIntegrator"/>
///         the supervisor's <c>merge</c> already drives (<c>.Integrate.cs</c>) to combine the producers' RECORDED
///         PATCHES onto a fresh run integration branch — works even when a producer never pushed a branch.</item>
///   <item>A producer manifest missing BOTH a branch and a patch (an I1 violation) → BLOCKED, loud reason, never a
///         silent default. An integration CONFLICT → BLOCKED, naming the conflicted files + the producers' own
///         preserved branches so the decider can reach for the EXISTING <c>resolve</c> verb next turn (see
///         <c>.Resolve.cs</c>'s widened <c>FindMostRecentConflictDecision</c> — no new escalation mechanism).</item>
/// </list>
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    /// <summary>Resolve one subtask's dependency staging. No declared dependency (<paramref name="dependsOn"/> empty) or no bound repository → <see cref="DependencyStagingResult.NoOverride"/> without touching the manifest store.</summary>
    private async Task<DependencyStagingResult> ResolveDependencyStagingAsync(IReadOnlyList<string> dependsOn, Guid? repositoryId, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        if (dependsOn.Count == 0 || repositoryId is not { } repoId) return DependencyStagingResult.NoOverride;

        var producerAgentRunIds = SupervisorDependencyGate.LatestSucceededAgentRunIds(context, dependsOn);

        if (producerAgentRunIds.Count == 0) return DependencyStagingResult.NoOverride;

        var producers = await ResolveProducerManifestsAsync(producerAgentRunIds, repoId, context.TeamId, cancellationToken).ConfigureAwait(false);

        if (producers.Count == 0) return DependencyStagingResult.NoOverride;   // every producer made no changes to THIS repo — nothing to hand off

        // A manifest row with NEITHER a pushed branch NOR an offloaded patch artifact has nothing this staging can
        // hand off — a small (below the 8KB inline-offload threshold) patch-only diff legitimately carries no
        // PatchArtifactId (it lives only in the producer's own AgentRunResult.Patch, which staging never reads —
        // the manifest, not the run result, is the single source of truth per I2), so BOTH must be absent before
        // this defends against the true I1 violation, never a silent clone of the default branch over real work.
        var missing = producers.Where(m => string.IsNullOrEmpty(m.Branch) && m.PatchArtifactId is null).ToList();

        if (missing.Count > 0)
            return BlockedResult($"producer(s) {string.Join(", ", missing.Select(m => m.AgentRunId))} recorded a diff but neither a branch nor a patch was captured for it — the handoff cannot proceed silently");

        if (producers.Count == 1 && !string.IsNullOrEmpty(producers[0].Branch))
            return new DependencyStagingResult { Ref = producers[0].Branch, GoalFoldText = FoldSingleProducer(producers[0]) };

        return await IntegrateProducersAsync(producers, repoId, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Each producer's manifest row for THIS repository (by RepositoryId; the sole row when a producer only ever touched one repo) — the durable branch/patch/summary handoff never re-derived from a decision's outcome JSON snapshot.</summary>
    private async Task<IReadOnlyList<Persistence.Entities.PublishManifest>> ResolveProducerManifestsAsync(IReadOnlyList<Guid> producerAgentRunIds, Guid repositoryId, Guid teamId, CancellationToken cancellationToken)
    {
        var manifests = new List<Persistence.Entities.PublishManifest>();

        foreach (var agentRunId in producerAgentRunIds)
        {
            var rows = await _manifests.ListForAgentRunAsync(agentRunId, teamId, cancellationToken).ConfigureAwait(false);

            var row = rows.FirstOrDefault(r => r.RepositoryId == repositoryId) ?? (rows.Count == 1 ? rows[0] : null);

            if (row is not null) manifests.Add(row);
        }

        return manifests;
    }

    /// <summary>Combine ≥2 producers' (or one patch-only producer's) recorded patches onto a fresh run integration branch via the SAME <see cref="IBranchIntegrator"/> the supervisor <c>merge</c> drives. Clean → that branch; anything else → BLOCKED, never a silent default.</summary>
    private async Task<DependencyStagingResult> IntegrateProducersAsync(IReadOnlyList<Persistence.Entities.PublishManifest> producers, Guid repositoryId, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var baseSha = producers.Select(p => p.BaseSha).FirstOrDefault(sha => !string.IsNullOrEmpty(sha));

        if (string.IsNullOrEmpty(baseSha))
            return BlockedResult("the producers recorded no base revision to integrate the handoff from");

        WorkspaceRequest? workspace;
        try
        {
            workspace = await _workspaces.ResolveByRepositoryIdAsync(repositoryId, context.TeamId, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            return BlockedResult($"the repository could not be resolved to stage the handoff: {ex.Message}");
        }

        if (workspace is null)
            return BlockedResult("the repository could not be resolved to a clone target");

        var contributions = new List<BranchContribution>(producers.Count);

        foreach (var producer in producers)
            contributions.Add(new BranchContribution
            {
                Label = producer.AgentRunId?.ToString() ?? producer.Id.ToString(),
                SourceRepositoryId = repositoryId,
                BaseSha = producer.BaseSha,
                Patch = await _offloader.ResolveAsync(context.TeamId, "", producer.PatchArtifactId, cancellationToken).ConfigureAwait(false),
                ProducedBranch = producer.Branch,
            });

        var request = new IntegrationRequest
        {
            TeamId = context.TeamId,
            RepositoryUrl = workspace.RepositoryUrl,
            BaseRef = workspace.Ref,
            BaseSha = baseSha!,
            Token = workspace.Token,
            TokenUsername = workspace.TokenUsername,
            // Per-turn, not per-run (mirrors .Integrate.cs's IntegrationBranch): a run may stage several dependency
            // handoffs across turns, each over a possibly larger/different producer set → each gets its own branch.
            IntegrationBranch = $"codespace/handoff/{context.SupervisorRunId:N}/turn{context.TurnNumber}",
            Depth = 0,
            Contributions = contributions,
        };

        IntegrationResult result;
        try
        {
            result = await _integrator.IntegrateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            return BlockedResult($"integrating the producers' work failed: {ex.Message}");
        }

        if (result.Status != IntegrationStatus.Clean || result.IntegratedBranch is not { Length: > 0 } branch)
            return BlockedResult(
                result.Reason ?? "the producers' work could not be auto-integrated onto one branch",
                conflictedFiles: result.Outcomes.SelectMany(o => o.ConflictedFiles).Distinct().ToList(),
                preservedBranches: result.Outcomes.Where(o => o.FallbackBranch is not null).Select(o => o.FallbackBranch!).ToList());

        return new DependencyStagingResult { Ref = branch, GoalFoldText = FoldIntegratedProducers(producers, branch) };
    }

    private static DependencyStagingResult BlockedResult(string reason, IReadOnlyList<string>? conflictedFiles = null, IReadOnlyList<string>? preservedBranches = null) => new()
    {
        BlockedReason = reason,
        ConflictedFiles = conflictedFiles ?? Array.Empty<string>(),
        PreservedBranches = preservedBranches ?? Array.Empty<string>(),
    };

    /// <summary>The server-authored handoff block for a single producer — deterministic, from durable data only (mirrors <see cref="SupervisorResolverRecipe"/>'s style), never model-authored.</summary>
    private static string FoldSingleProducer(Persistence.Entities.PublishManifest producer) =>
        $"You are building on prior work already on branch `{producer.Branch}`" +
        (string.IsNullOrWhiteSpace(producer.Summary) ? "" : $": {producer.Summary}") +
        $" ({producer.ChangedFileCount} file(s) changed). Continue from this branch — do not start from the repository's default branch.";

    /// <summary>The server-authored handoff block for ≥2 (or one patch-only) producers now combined onto <paramref name="integratedBranch"/> — names every contributing producer so the agent understands what it inherits.</summary>
    private static string FoldIntegratedProducers(IReadOnlyList<Persistence.Entities.PublishManifest> producers, string integratedBranch)
    {
        var lines = producers.Select(p => $"- {(string.IsNullOrWhiteSpace(p.Summary) ? "(no summary)" : p.Summary)} ({p.ChangedFileCount} file(s) changed)");

        return $"You are building on prior work from {producers.Count} producer(s), already integrated onto branch `{integratedBranch}`:\n" +
               string.Join("\n", lines) +
               "\nContinue from this branch — do not start from the repository's default branch.";
    }
}
