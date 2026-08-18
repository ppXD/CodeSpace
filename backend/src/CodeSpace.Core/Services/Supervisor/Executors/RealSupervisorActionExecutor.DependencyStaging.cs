using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

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
///   <item>A producer with NO branch, NO offloaded patch artifact and NO inline patch (an I1 violation) → BLOCKED,
///         loud reason, never a silent default. All three carriers are asked through the shared
///         <see cref="Agents.Publish.IAgentPatchReader"/>, because patch offload is SIZE-gated: a sub-threshold diff
///         is recorded ONLY in the producing run's result, so a manifest-only check reads it as lost work.</item>
///   <item>An integration CONFLICT → BLOCKED, naming the conflicted files + the producers' own
///         preserved branches so the decider can reach for the EXISTING <c>resolve</c> verb next turn (see
///         <c>.Resolve.cs</c>'s widened <c>FindMostRecentConflictDecision</c> — no new escalation mechanism).</item>
/// </list>
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    /// <summary>Resolve one subtask's dependency staging. No declared dependency (<paramref name="dependsOn"/> empty) or no bound repository → <see cref="DependencyStagingResult.NoOverride"/> without touching the manifest store.</summary>
    private async Task<DependencyStagingResult> ResolveDependencyStagingAsync(IReadOnlyList<string> dependsOn, Guid? repositoryId, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        if (dependsOn.Count == 0 || repositoryId is not { } repoId) return NoStaging(dependsOn, repositoryId, 0, 0, context);

        var producerAgentRunIds = SupervisorDependencyGate.LatestSucceededAgentRunIds(context, dependsOn);

        if (producerAgentRunIds.Count == 0) return NoStaging(dependsOn, repoId, 0, 0, context);

        var producers = await ResolveProducerManifestsAsync(producerAgentRunIds, repoId, context.TeamId, cancellationToken).ConfigureAwait(false);

        if (producers.Count == 0) return NoStaging(dependsOn, repoId, producerAgentRunIds.Count, 0, context);   // every producer made no changes to THIS repo — nothing to hand off

        var missing = await ProducersWithNothingToHandOffAsync(producers, context.TeamId, cancellationToken).ConfigureAwait(false);

        if (missing.Count > 0)
            return BlockedStaging($"producer(s) {string.Join(", ", missing.Select(m => m.AgentRunId))} recorded a diff but captured no branch, no patch artifact and no inline patch — the handoff cannot proceed silently", context);

        if (producers.Count == 1 && !string.IsNullOrEmpty(producers[0].Branch))
        {
            _logger.LogInformation("Supervisor dependency staging pinned turn {Turn} on node {NodeId} to the lone producer's branch {Ref}", context.TurnNumber, context.NodeId, producers[0].Branch);
            return new DependencyStagingResult { Ref = producers[0].Branch, GoalFoldText = FoldSingleProducer(producers[0]) };
        }

        return await IntegrateProducersAsync(producers, repoId, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The producers this staging genuinely cannot hand off — the fail-closed I1 guard, asked of ALL THREE carriers
    /// rather than only the two the manifest names. A row with no branch and no <c>PatchArtifactId</c> is not yet
    /// evidence of lost work: patch offload is SIZE-gated, so a diff at or below the artifact store's inline
    /// threshold is recorded ONLY in the producing run's result. Consulting the manifest alone therefore blocked
    /// every sub-threshold handoff while reporting it as a data-integrity violation. Only a producer whose inline
    /// carrier is ALSO empty has nothing to hand off, and only that one blocks the spawn.
    /// </summary>
    private async Task<IReadOnlyList<Persistence.Entities.PublishManifest>> ProducersWithNothingToHandOffAsync(IReadOnlyList<Persistence.Entities.PublishManifest> producers, Guid teamId, CancellationToken cancellationToken)
    {
        var empty = new List<Persistence.Entities.PublishManifest>();

        foreach (var producer in producers.Where(m => string.IsNullOrEmpty(m.Branch) && m.PatchArtifactId is null))
        {
            var inline = await _patches.ReadAsync(teamId, PatchSourceFor(producer), cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(inline)) empty.Add(producer);
        }

        return empty;
    }

    /// <summary>Where THIS producer's diff bytes live, as the shared <see cref="Agents.Publish.IAgentPatchReader"/> reads them: the offloaded artifact when the manifest names one, else the producing run's own inline patch under this row's alias.</summary>
    internal static AgentPatchSource PatchSourceFor(Persistence.Entities.PublishManifest producer) => new()
    {
        AgentRunId = producer.AgentRunId,
        RepositoryAlias = producer.RepositoryAlias,
        PatchArtifactId = producer.PatchArtifactId,
    };

    /// <summary>
    /// WHICH gate a staging no-op fell through, as a named reason. Pure + <c>internal static</c> so the LADDER is
    /// unit-pinned (the same reason <see cref="BuildBlockedSpawnOutcome"/> is), because these four exits are otherwise
    /// indistinguishable: all three no-op arms return the SAME <see cref="DependencyStagingResult.NoOverride"/>
    /// singleton, which produces a task with a null Workspace — byte-identical to a subtask that declared no
    /// dependency at all. A live run that hands off nothing therefore left no trace of WHY, which is exactly how
    /// real-model run 30775218538 cost a forensic pass that still could not name the gate.
    ///
    /// <para>Callers pass the counts they have established SO FAR; the ladder short-circuits in the same order the
    /// resolver evaluates them, so a partially-populated call can never report a later gate than the one it reached.
    /// Null ⇒ staging proceeds (a ref will be resolved or the spawn blocked).</para>
    /// </summary>
    internal static string? DependencyStagingNoOpReason(IReadOnlyList<string> dependsOn, Guid? repositoryId, int succeededProducerCount, int manifestCount)
    {
        if (dependsOn.Count == 0) return "the subtask declared no dependsOn edge";

        if (repositoryId is null) return "the subtask is bound to no repository";

        if (succeededProducerCount == 0) return $"none of its {dependsOn.Count} declared dependency(ies) [{string.Join(", ", dependsOn)}] resolved to a non-rejected succeeded attempt";

        if (manifestCount == 0) return $"{succeededProducerCount} producer run(s) succeeded but none recorded a publish manifest for this repository";

        return null;
    }

    /// <summary>Name the gate this no-op fell through in the run's log, then return the (indistinguishable) no-override singleton — the staging sibling of <c>.Resolve.cs</c>'s named-reason no-op line.</summary>
    private DependencyStagingResult NoStaging(IReadOnlyList<string> dependsOn, Guid? repositoryId, int succeededProducerCount, int manifestCount, SupervisorTurnContext context)
    {
        var reason = DependencyStagingNoOpReason(dependsOn, repositoryId, succeededProducerCount, manifestCount);

        _logger.LogInformation("Supervisor dependency staging is a no-op at turn {Turn} on node {NodeId} ({Reason}) — the dependent clones the repository default branch", context.TurnNumber, context.NodeId, reason);

        return DependencyStagingResult.NoOverride;
    }

    /// <summary>Withhold the spawn, naming the reason in the log as well as the outcome — the block is already loud in <c>blockedSubtasks</c>, but nothing read that key, so a blocked handoff was invisible in the run's log.</summary>
    private DependencyStagingResult BlockedStaging(string reason, SupervisorTurnContext context)
    {
        _logger.LogWarning("Supervisor dependency staging BLOCKED the spawn at turn {Turn} on node {NodeId} ({Reason})", context.TurnNumber, context.NodeId, reason);

        return BlockedResult(reason);
    }

    /// <summary>
    /// Retry world-state conservation (P0-1): resolve a RETRIED subtask's OWN prior attempt continuity ref — the
    /// branch that attempt actually pushed, per its <see cref="Persistence.Entities.PublishManifest"/> row — so the
    /// retry's sandbox is checked out AT that committed work, never a fresh clone of the repository's default branch
    /// (the root-cause forensic finding of run 96695645). <paramref name="priorAgentRunId"/> MUST be the SAME attempt
    /// the caller is about to resume the conversation from (when one exists) — never independently re-resolved —
    /// so a resume hint's "your git changes were/weren't preserved" claim always describes the attempt it actually
    /// restores, not a different, unrelated one. No prior attempt id, no manifest row for this repository, or a
    /// manifest with no pushed branch (patch-only, or acceptance never got that far) →
    /// <see cref="DependencyStagingResult.NoOverride"/> — the default-branch clone stands, and the caller must make
    /// the resume hint say so honestly.
    /// </summary>
    private async Task<DependencyStagingResult> ResolvePriorAttemptStagingAsync(Guid? priorAgentRunId, Guid? repositoryId, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        if (repositoryId is not { } repoId || priorAgentRunId is not { } runId) return DependencyStagingResult.NoOverride;

        var rows = await _manifests.ListForAgentRunAsync(runId, context.TeamId, cancellationToken).ConfigureAwait(false);
        var manifest = rows.FirstOrDefault(r => r.RepositoryId == repoId) ?? (rows.Count == 1 ? rows[0] : null);

        if (manifest is null || string.IsNullOrEmpty(manifest.Branch)) return DependencyStagingResult.NoOverride;

        return new DependencyStagingResult { Ref = manifest.Branch, GoalFoldText = FoldPriorAttempt(manifest) };
    }

    /// <summary>The server-authored continuity block for a retry whose prior attempt already pushed a branch — deterministic, from durable data only, mirroring <see cref="FoldSingleProducer"/>'s style.</summary>
    private static string FoldPriorAttempt(Persistence.Entities.PublishManifest priorAttempt) =>
        $"Your PRIOR attempt already committed work on branch `{priorAttempt.Branch}`" +
        (string.IsNullOrWhiteSpace(priorAttempt.Summary) ? "" : $": {priorAttempt.Summary}") +
        $" ({priorAttempt.ChangedFileCount} file(s) changed). This workspace is checked out AT that branch — do not redo work already present here.";

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
            return BlockedStaging("the producers recorded no base revision to integrate the handoff from", context);

        WorkspaceRequest? workspace;
        try
        {
            workspace = await _workspaces.ResolveByRepositoryIdAsync(repositoryId, context.TeamId, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            return BlockedStaging($"the repository could not be resolved to stage the handoff: {ex.Message}", context);
        }

        if (workspace is null)
            return BlockedStaging("the repository could not be resolved to a clone target", context);

        var contributions = new List<BranchContribution>(producers.Count);

        foreach (var producer in producers)
            contributions.Add(new BranchContribution
            {
                Label = ProducerLabel(producer),
                SourceRepositoryId = repositoryId,
                BaseSha = producer.BaseSha,
                Patch = await _patches.ReadAsync(context.TeamId, PatchSourceFor(producer), cancellationToken).ConfigureAwait(false),
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
            IntegrationBranch = HandoffIntegrationBranch(context.SupervisorRunId, context.TurnNumber, repositoryId, contributions.Select(c => c.Label)),
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
            return BlockedStaging($"integrating the producers' work failed: {ex.Message}", context);
        }

        if (result.Status != IntegrationStatus.Clean || result.IntegratedBranch is not { Length: > 0 } branch)
        {
            var conflicted = result.Outcomes.SelectMany(o => o.ConflictedFiles).Distinct().ToList();

            // Not routed through BlockedStaging: this arm carries the conflict detail the decider's `resolve` verb
            // needs, and naming the conflicted files in the log is what makes a staging-time conflict diagnosable
            // without re-reading the integration branch.
            _logger.LogWarning("Supervisor dependency staging BLOCKED the spawn at turn {Turn} on node {NodeId} — the {Count} producer(s) could not be auto-integrated ({Reason}); conflicted: {Conflicted}",
                context.TurnNumber, context.NodeId, producers.Count, result.Reason ?? "no reason reported", conflicted.Count == 0 ? "(none reported)" : string.Join(", ", conflicted));

            return BlockedResult(
                result.Reason ?? "the producers' work could not be auto-integrated onto one branch",
                conflictedFiles: conflicted,
                preservedBranches: result.Outcomes.Where(o => o.FallbackBranch is not null).Select(o => o.FallbackBranch!).ToList());
        }

        _logger.LogInformation("Supervisor dependency staging integrated {Count} producer(s) onto {Ref} at turn {Turn} on node {NodeId}", producers.Count, branch, context.TurnNumber, context.NodeId);

        return new DependencyStagingResult { Ref = branch, GoalFoldText = FoldIntegratedProducers(producers, branch) };
    }

    /// <summary>
    /// The handoff integration branch for ONE dependent, discriminated by WHAT it integrates — the target repository
    /// and the producer set — rather than by the turn alone. Keyed on run + turn only, EVERY dependent staged in one
    /// turn asked for the SAME name — so the second one's integration found a remote branch carrying the FIRST one's
    /// (different) tree and <see cref="IBranchIntegrator"/>'s no-clobber reconcile correctly refused it, which
    /// <c>.Spawn.cs</c> correctly turns into a whole-turn abort. A manufactured name collision therefore staged ZERO
    /// agents, including the dependent whose own staging was clean. Nothing about those two rules changes here; the
    /// collision does.
    ///
    /// <para><b>Idempotent re-push</b> (the property the reconcile needs): the digest is a pure content hash of the
    /// repository and the producer identities, so an identical (repository, set) always yields an identical name and
    /// re-integrates the identical patches, and the reconcile short-circuits on tree equality exactly as before —
    /// which is also why the discriminator is the producer set and NOT anything per-dependent (a subtask id, an
    /// index, a fresh guid would fork a second branch for the same work and break re-execution of the same turn).
    /// <b>Distinctness</b>: different sets, and the same set against a different repository, hash differently, so
    /// they never contend for one ref at all.</para>
    ///
    /// <para>A shared name is never a shared TREE by assumption: the apply is order-sensitive
    /// (<c>LocalGitBranchIntegrator.ApplyAllAsync</c> runs <c>git apply --3way</c> in the declared order, which does
    /// not commute in general), so two dependents that share a name can still integrate to different trees — and
    /// <c>ReconcileExistingBranchAsync</c> is gated on tree equality, not on the name, so the second one is REFUSED
    /// and its staging blocks. That is the same degradation the per-turn name already produced, and it is the reason
    /// this change is safe to make without touching the reconcile: the failure mode stays a block, never a graft.</para>
    ///
    /// <para>The turn number stays for the operator scanning branches, and keeps this change purely ADDITIVE — a name
    /// written before it (<c>…/turn4</c>) can never equal one written after (<c>…/turn4-{12 hex}</c>), so a run
    /// resuming across the change creates its branch instead of colliding with the one its own earlier turn pushed.</para>
    /// </summary>
    internal static string HandoffIntegrationBranch(Guid supervisorRunId, int turnNumber, Guid repositoryId, IEnumerable<string> producerLabels) =>
        $"codespace/handoff/{supervisorRunId:N}/turn{turnNumber}-{ProducerSetDigest(repositoryId, producerLabels)}";

    /// <summary>
    /// A short, deterministic digest of WHAT this handoff integrates: SHA-256 over the target repository followed by
    /// the newline-joined ordinal-sorted producer labels, truncated to 12 lowercase hex chars (48 bits — a collision
    /// within one run's handoffs is not a real risk, and the branch stays readable).
    ///
    /// <para>The <b>repository</b> is in it because a producer label is an agent RUN id, which is repository-agnostic,
    /// while <c>.Spawn.cs</c> resolves the target repository PER SUBTASK — so two dependents in one turn can declare
    /// the identical <c>dependsOn</c> and still target different repositories, for which
    /// <see cref="ResolveProducerManifestsAsync"/> selects different manifest rows and therefore integrates different
    /// patches onto a different tree. Two repository rows may carry ONE clone URL (the repositories table is unique on
    /// provider instance + external id, never on URL), and there those two trees would meet on one ref.</para>
    ///
    /// <para>The labels are <b>sorted</b> because the order a plan happened to declare its <c>dependsOn</c> in is
    /// re-derived every turn by <see cref="SupervisorDependencyGate.LatestSucceededAgentRunIds"/>, so an
    /// order-sensitive name would fork a redundant branch for the identical set. A content hash rather than
    /// <c>GetHashCode</c>, which .NET randomizes per process — a run resuming on another worker must compute the
    /// same name for the same producers or the idempotent re-push above silently stops holding.</para>
    /// </summary>
    private static string ProducerSetDigest(Guid repositoryId, IEnumerable<string> producerLabels)
    {
        var canonical = string.Join("\n", producerLabels.OrderBy(label => label, StringComparer.Ordinal).Prepend(repositoryId.ToString("N")));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..ProducerSetDigestLength].ToLowerInvariant();
    }

    private const int ProducerSetDigestLength = 12;

    /// <summary>The identity ONE producer contributes under — its agent run, else its own manifest row. Shared by the contribution's <see cref="BranchContribution.Label"/> and the branch digest, so the branch name can never name a different set than the one actually applied onto it.</summary>
    internal static string ProducerLabel(Persistence.Entities.PublishManifest producer) => producer.AgentRunId?.ToString() ?? producer.Id.ToString();

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
