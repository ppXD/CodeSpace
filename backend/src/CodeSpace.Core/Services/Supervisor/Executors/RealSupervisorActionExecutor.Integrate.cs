using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// SOTA #3 — the OPT-IN integrate + synthesis augmentation of the supervisor <c>merge</c> (Rule 10 <c>.Integrate.cs</c>).
/// When the integrate gate is on, the deterministic fold (<c>.Merge.cs</c>) is augmented so multi-agent fan-out
/// INTEGRATES rather than narrates:
/// <list type="bullet">
///   <item><b>integration</b> (model-free) — the K agents' diffs are integrated ON DISK into one reviewable branch via
///         <see cref="IBranchIntegrator"/>, base-anchored + fail-safe (a conflict keeps the K branches + reports it; no
///         corrupt merge, no clobber). Best-effort: a git infrastructure failure is recorded, never crashes the turn.</item>
///   <item><b>synthesis</b> (model) — a 2nd <see cref="ILLMClient"/> reduce over the K REAL diffs producing a coherent
///         combined summary. Validated now with a deterministic fake at the <see cref="ILLMClient"/> seam (it proves the
///         diffs are threaded into the prompt); real-model quality is deferred to the cassette tier.</item>
/// </list>
///
/// <para>The gate is fail-closed (<see cref="AgentRunExecutor.ShouldIntegrate"/> — the ambient env flag OR the profile's
/// per-run opt-in). With it OFF the outcome is byte-identical to pre-SOTA-#3: no clone, no LLM call, just the fold.</para>
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    /// <summary>Layer the integration + synthesis keys onto the fold outcome — ONLY when the gate is on AND there are agents to combine. A no-op (returns immediately) keeps the gate-OFF path byte-identical. <paramref name="forcedByPublishGate"/> (I3) bypasses the operator's integrate opt-in entirely — a correctness floor is never left off — and skips synthesis (the server is trying to PUBLISH, not narrate; no LLM call the operator didn't ask for).</summary>
    private async Task AugmentWithIntegrationAndSynthesisAsync(Dictionary<string, object?> outcome, SupervisorTurnContext context, IReadOnlyList<MergedAgent> merged, bool forcedByPublishGate, CancellationToken cancellationToken)
    {
        var profile = context.AgentProfile;

        if (!forcedByPublishGate && !AgentRunExecutor.ShouldIntegrate(perRunOptIn: profile?.IntegrateBranches == true)) return;
        if (merged.Count == 0) return;

        if (!forcedByPublishGate)
            // Synthesis (facet b) reads the diffs — no repo needed; runs whenever the gate is on. Best-effort: a model
            // failure degrades to a note, NEVER crashing the merge turn (which would strand the decision row Running).
            outcome["synthesis"] = await TrySynthesizeAsync(context.TeamId, context.Goal, merged, profile, cancellationToken).ConfigureAwait(false);

        // Integration (facet a) writes a branch — only with a resolvable repository. EXCEPT when the conflict was
        // already RESOLVED: a VERIFIED resolver's own tested branch IS the reconciled merge, so re-running the
        // integrator over the original conflicting branches would just re-conflict (resolver loop #379, S5). In that
        // case surface the resolver branch as a Clean integration without touching git. A MULTI-repo run (the agents
        // produced per-repo results) integrates EACH writable repo on its own axis (S7-C); a single-repo run keeps the
        // byte-identical flat path.
        if (profile?.RepositoryId is { } repoId)
            outcome["integration"] = AcceptedResolutionBranch(context) is { } resolvedBranch
                ? ResolutionIntegration(resolvedBranch)
                : HasPerRepoResults(merged)
                    ? await IntegrateMultiRepoAsync(context, merged, cancellationToken).ConfigureAwait(false)
                    : await IntegrateMergedAsync(repoId, context, merged, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether this merge spans MULTIPLE repositories — ANY merged agent recorded per-repo results (a multi-repo workspace run). A single-repo run has none, so it takes the byte-identical flat <see cref="IntegrateMergedAsync"/> path.</summary>
    private static bool HasPerRepoResults(IReadOnlyList<MergedAgent> merged) => merged.Any(m => m.RepositoryResults.Count > 0);

    /// <summary>
    /// The SAME publish guard chain <c>AgentRunExecutor</c>'s per-agent push evaluates (Order ascending, first
    /// non-null wins) — so an integration's push respects the identical repo policy (<c>PublishMode.PatchOnly</c>)
    /// / no-credential floor, never just the per-agent push. A neutral task (no <see cref="AgentTask.PushProducedBranch"/>
    /// opt-out) is evaluated against the resolved repo — a merge has no per-task opt-out concept, so only the
    /// repo-scoped guards (credential, policy) can ever fire here. Null when the repo can't be resolved (a
    /// different Skipped path already names that) or no guard blocks.
    /// </summary>
    private async Task<PublishGuardVerdict?> EvaluatePublishGuardAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repository = await _db.Repository.AsNoTracking().SingleOrDefaultAsync(r => r.Id == repositoryId, cancellationToken).ConfigureAwait(false);

        if (repository is null) return null;

        var neutralTask = new AgentTask { Goal = "", Harness = "" };

        foreach (var guard in _publishGuards)
            if (guard.Evaluate(neutralTask, repository) is { } verdict)
                return verdict;

        return null;
    }

    /// <summary>
    /// The branch a VERIFIED resolver produced — but ONLY when the most recent agent-staging decision (spawn / retry /
    /// resolve) was a verified <c>resolve</c> (resolver loop #379, S5). That means the conflict is reconciled into the
    /// resolver's OWN tested branch and a <c>merge</c> must surface IT, not re-integrate. Null when the latest staging
    /// was a normal spawn/retry (there's fresh agent work to combine → run the integrator) or the resolution wasn't
    /// verified (the safety floor already withheld acceptance). Reads only durable folded state — pure + replay-safe.
    /// </summary>
    private static string? AcceptedResolutionBranch(SupervisorTurnContext context)
    {
        var lastStaging = context.PriorDecisions.LastOrDefault(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind));

        // The most-recent agent-staging decision must ITSELF be the accepted resolution — a spawn/retry after it means
        // fresh work to combine, so the integrator runs. The "verified resolve → its branch" rule is the SHARED
        // SupervisorOutcome.ResolvedBranch (the same encoding the node-output reader uses, so the two never drift).
        return lastStaging is null ? null : SupervisorOutcome.ResolvedBranch(lastStaging);
    }

    /// <summary>
    /// The PER-REPO reconciled branches a VERIFIED MULTI-repo resolution contributes (resolver loop #379, S7-D2),
    /// keyed by repository id — but ONLY when the most recent agent-staging decision IS that verified resolve (the
    /// same disqualifier as <see cref="AcceptedResolutionBranch"/>: a spawn/retry after it means fresh work). The
    /// multi-repo analogue of <see cref="AcceptedResolutionBranch"/>, off the SHARED <see cref="SupervisorOutcome.ResolvedRepositoryBranches"/>
    /// (so the acceptance rule never drifts — Rule 7). Empty for a single-repo / unverified / non-resolve last staging.
    /// </summary>
    private static IReadOnlyDictionary<Guid, string> AcceptedResolutionRepositories(SupervisorTurnContext context)
    {
        var lastStaging = context.PriorDecisions.LastOrDefault(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind));

        if (lastStaging is null) return EmptyResolution;

        return SupervisorOutcome.ResolvedRepositoryBranches(lastStaging)
            .Where(b => b.RepositoryId is not null)
            .ToDictionary(b => b.RepositoryId!.Value, b => b.SourceBranch);
    }

    private static readonly IReadOnlyDictionary<Guid, string> EmptyResolution = new Dictionary<Guid, string>();

    /// <summary>One repo's ACCEPTED-resolution block (S7-D2): the resolver's reconciled branch surfaced as a Clean per-repo block WITHOUT re-running the integrator (which would re-conflict). Carries the same <c>baseBranch</c> PR target as <see cref="ProjectRepoBlock"/> (S7-E) so the node-output reader binds it. <c>via</c>/<c>reason</c> are descriptive audit only — the gate is the resolve verdict via <see cref="SupervisorOutcome.ResolvedRepositoryBranches"/>. Shape-compatible with <see cref="ProjectRepoBlock"/> so <see cref="SupervisorOutcome.ReadFinalRepositoryBranches"/> reads it uniformly.</summary>
    private static object ResolvedRepoBlock((Guid RepositoryId, string Alias) repo, string resolvedBranch, string baseBranch) => new
    {
        repositoryId = repo.RepositoryId,
        alias = repo.Alias,
        status = "Clean",
        integratedBranch = resolvedBranch,
        baseBranch,
        via = "resolution",
        reason = "a verified resolver agent reconciled this repository's conflicting branches into one tested branch",
    };

    /// <summary>
    /// The integration block for an ACCEPTED resolution (S5): the resolver's own tested branch surfaced as a Clean
    /// integration WITHOUT re-running the integrator. <c>status</c> + <c>integratedBranch</c> are the load-bearing
    /// fields (read by <see cref="SupervisorOutcome.ReadIntegration"/>); <c>via</c> + <c>reason</c> are DESCRIPTIVE
    /// audit metadata in the persisted ledger only — no reader or gate branches on them (the acceptance decision is
    /// keyed off the resolve verdict via <see cref="SupervisorOutcome.ResolvedBranch"/>, NEVER off <c>via</c>). No
    /// <c>outcomes</c> array (no per-contribution apply happened — the resolver did the reconciliation).
    /// </summary>
    private static object ResolutionIntegration(string resolvedBranch) => new
    {
        status = "Clean",
        integratedBranch = resolvedBranch,
        via = "resolution",   // descriptive audit only (see doc) — no code branches on this; the gate is the resolve verdict
        reason = "a verified resolver agent reconciled the conflicting branches into one tested branch",
    };

    // ── Facet (b): the model synthesis reduce over the K real diffs ──────────────────

    /// <summary>Best-effort wrapper: synthesis is a non-essential enrichment, so ANY failure (a model 4xx/5xx, a missing pool model, a transport / serialization fault) degrades to a note — it must never escape and strand the turn. Cancellation still propagates.</summary>
    private async Task<object> TrySynthesizeAsync(Guid teamId, string goal, IReadOnlyList<MergedAgent> merged, SupervisorAgentProfile? profile, CancellationToken cancellationToken)
    {
        try
        {
            return await SynthesizeAsync(teamId, goal, merged, profile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Supervisor synthesis reduce failed; keeping the deterministic fold + on-disk integration");
            return new { note = "synthesis unavailable", error = ex.Message };
        }
    }

    private async Task<object> SynthesizeAsync(Guid teamId, string goal, IReadOnlyList<MergedAgent> merged, SupervisorAgentProfile? profile, CancellationToken cancellationToken)
    {
        // The synthesis is a plain-TEXT reduce, so it prefers a dedicated text-completion provider (the established
        // synth seam) and falls back to any registered client — in production the structured-capable provider also
        // serves text. This intentionally differs from the decider/planner's structured-first resolution: those NEED
        // structured output; a text reduce does not. A deployment with no LLM provider degrades to a note.
        var client = _llm.All.FirstOrDefault(c => c is not IStructuredLLMClient) ?? _llm.All.FirstOrDefault();

        if (client is null) return new { note = "no LLM provider available for synthesis" };

        // Pure pool-driven (S6b): the model + credential come from the team's pool for the chosen client's provider —
        // the profile's model is a PIN (it must be a qualifying pool model), else the pool's recommended one. A text
        // reduce doesn't need structured output. No pool model → degrade to a note (never an env key, never a default).
        var pick = await _modelSelector.SelectAsync(teamId, client.Provider, allowedModels: null, pinnedModel: profile?.Model, cancellationToken).ConfigureAwait(false);

        if (pick is null) return new { note = $"no pool model available for synthesis on provider '{client.Provider}'" };

        var request = new LLMCompletionRequest
        {
            Model = pick.ModelId,
            Credential = pick.Credential,
            SystemPrompt = "You are combining the work of several parallel coding agents into ONE coherent change. Each agent's unified diff follows. Produce a concise synthesis: what the combined change does, how the pieces fit, and any overlaps or risks a reviewer should check. Do not invent changes that are not in the diffs.",
            UserPrompt = BuildSynthesisPrompt(goal, merged),
        };

        var completion = await client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        return new { text = completion.Text, model = completion.Model };
    }

    /// <summary>The synthesis user prompt — the goal + each agent's status/summary AND its REAL unified diff (the hunk bodies), so the reduce reasons over what the agents actually changed, not just their self-reported summaries.</summary>
    private static string BuildSynthesisPrompt(string goal, IReadOnlyList<MergedAgent> merged)
    {
        var sb = new StringBuilder();
        sb.Append("Goal: ").Append(goal).Append("\n\n");

        foreach (var a in merged)
        {
            sb.Append("=== Agent ").Append(a.AgentRunId).Append(" (").Append(a.Status).Append(") ===\n");
            if (!string.IsNullOrWhiteSpace(a.Summary)) sb.Append("Summary: ").Append(a.Summary).Append('\n');

            // Multi-repo: narrate EACH repo's real diff (so the synthesis covers the whole change set, not just the
            // primary repo's top-level patch); a single-repo agent (no per-repo results) keeps the byte-identical
            // top-level Diff: section.
            if (a.RepositoryResults.Count > 0)
                foreach (var repo in a.RepositoryResults.Where(r => !IsUntouched(r)))
                    sb.Append("Diff [").Append(repo.Alias).Append("]:\n").Append(string.IsNullOrEmpty(repo.Patch) ? "(no diff captured)" : repo.Patch).Append("\n\n");
            else
                sb.Append("Diff:\n").Append(string.IsNullOrEmpty(a.Patch) ? "(no diff captured)" : a.Patch).Append("\n\n");
        }

        return sb.ToString();
    }

    // ── Facet (a): the model-free on-disk integration ───────────────────────────────

    private async Task<object> IntegrateMergedAsync(Guid repoId, SupervisorTurnContext context, IReadOnlyList<MergedAgent> merged, CancellationToken cancellationToken)
    {
        // The resolver THROWS WorkspaceException for a deleted / cross-team / no-clone-URL repo (it never returns null
        // for those), so the resolve is wrapped: an unresolvable repo degrades to a Skipped outcome — it must never
        // escape and strand the merge decision Running (the turn service only catches AgentDefinitionResolutionException).
        WorkspaceRequest? workspace;
        try
        {
            workspace = await _workspaces.ResolveByRepositoryIdAsync(repoId, context.TeamId, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            _logger.LogWarning(ex, "Supervisor branch integration could not resolve the repository; keeping the side-by-side fold");
            return new { status = "Skipped", reason = ex.Message };
        }

        if (workspace is null) return new { status = "Skipped", reason = "the repository could not be resolved to a clone target" };

        // Only agents that recorded a base AND captured actual work (a patch or a produced branch) can be integrated by
        // patch — a failed / abandoned / analysis-only agent is EXCLUDED so it can't sink the whole clean set. A FAILED
        // agent often DID record a base (its workspace was cloned before it failed) yet has no patch/branch, so checking
        // the base alone let it through as an UNINTEGRABLE contribution that conflicted the entire merge (e.g. after a
        // failure→retry, the failed first attempt blocked integrating the successful retry). Require captured work too.
        // Surfaced honestly as `excludedAgents` so the outcome stays truthful about who combined.
        var eligible = merged.Where(IsIntegrable).ToList();
        var excluded = merged.Where(m => !IsIntegrable(m)).Select(m => m.AgentRunId.ToString()).ToList();

        if (eligible.Count == 0) return new { status = "Skipped", reason = "no agent recorded a base revision (an analysis-only run has nothing to integrate)", excludedAgents = excluded };

        if (await EvaluatePublishGuardAsync(repoId, cancellationToken).ConfigureAwait(false) is { } guardVerdict)
            return new { status = "Skipped", reason = $"publish policy: {guardVerdict.Reason}", excludedAgents = excluded };

        var request = BuildIntegrationRequest(repoId, context, workspace, eligible[0].BaseSha!, eligible);

        try
        {
            var result = await _integrator.IntegrateAsync(request, cancellationToken).ConfigureAwait(false);
            return ProjectIntegrationResult(result, excluded);
        }
        catch (WorkspaceException ex)
        {
            // Best-effort: a git infrastructure failure (auth / network) is recorded (token already redacted by the
            // integrator) but NEVER crashes the merge turn — the deterministic fold + the K branches remain.
            _logger.LogWarning(ex, "Supervisor branch integration failed; keeping the side-by-side fold");
            return new { status = "Failed", reason = ex.Message, excludedAgents = excluded };
        }
    }

    /// <summary>An agent is integrable only if it recorded a base AND captured actual work (a patch or a produced branch). A failed agent that cloned a base but produced nothing is NOT a contribution — its empty patch is Unintegrable and would conflict the whole merge.</summary>
    private static bool IsIntegrable(MergedAgent m) => !string.IsNullOrEmpty(m.BaseSha) && (!string.IsNullOrEmpty(m.Patch) || !string.IsNullOrEmpty(m.ProducedBranch));

    private static IntegrationRequest BuildIntegrationRequest(Guid repoId, SupervisorTurnContext context, WorkspaceRequest workspace, string baseSha, IReadOnlyList<MergedAgent> eligible) => new()
    {
        TeamId = context.TeamId,
        RepositoryUrl = workspace.RepositoryUrl,
        BaseRef = workspace.Ref,
        BaseSha = baseSha,
        Token = workspace.Token,
        TokenUsername = workspace.TokenUsername,
        // Per-MERGE-TURN branch (not per-run): a supervisor may merge repeatedly (spawn→merge→spawn→merge), each over a
        // strictly larger agent set → a different tree. A run-id-only name would pin the branch to wave 1 and refuse
        // every later, larger merge as "advanced". The turn discriminator gives each wave its own reviewable branch
        // while a re-executed SAME turn maps to the same branch (the tree-equality idempotence no-op still holds).
        IntegrationBranch = $"codespace/integration/{context.SupervisorRunId:N}/turn{context.TurnNumber}",
        Depth = 0,
        Contributions = eligible.Select(m => new BranchContribution
        {
            Label = m.AgentRunId.ToString(),
            SourceRepositoryId = repoId,
            BaseSha = m.BaseSha,
            Patch = m.Patch,            // already resolved in .Merge.cs (offloaded diffs folded back) → no artifact id
            ProducedBranch = m.ProducedBranch,
        }).ToList(),
    };

    private static object ProjectIntegrationResult(IntegrationResult result, IReadOnlyList<string> excludedAgents) => new
    {
        status = result.Status.ToString(),
        integratedBranch = result.IntegratedBranch,
        appliedCount = result.AppliedCount,
        reason = result.Reason,
        excludedAgents,
        outcomes = ProjectOutcomes(result),
    };

    /// <summary>The per-contribution outcomes array — the ONE projection both the single-repo flat block (<see cref="ProjectIntegrationResult"/>) and the per-repo block (<see cref="ProjectRepoBlock"/>) emit, so the two shapes can't drift (the write-side analogue of the read-side <see cref="SupervisorOutcome.ReadIntegration"/> unification).</summary>
    private static object ProjectOutcomes(IntegrationResult result) =>
        result.Outcomes.Select(o => new { label = o.Label, disposition = o.Disposition.ToString(), reason = o.Reason, conflictedFiles = o.ConflictedFiles, fallbackBranch = o.FallbackBranch }).ToList();

    // ── Facet (a), multi-repo: integrate EACH writable repo on its own axis (resolver loop #379, S7-C) ──────────────

    /// <summary>
    /// Multi-repo integration: run the patch-based <see cref="IBranchIntegrator"/> ONCE PER WRITABLE repo — each a
    /// single-repo set over that repo's per-repo contributions — and aggregate the per-repo blocks into one integration
    /// outcome. A repo with no resolvable clone / no recorded base is Skipped (named), a git infrastructure failure is
    /// Failed (named); neither crashes the turn — the fail-safe is per repo, exactly like the single-repo path. The
    /// aggregate <c>status</c> is the worst per-repo outcome, so the decider perceives "a conflict" off the SAME
    /// <see cref="SupervisorOutcome.ReadIntegration"/> the single-repo path feeds; the per-repo detail rides in
    /// <c>repositories[]</c> for the per-repo resolution loop (S7-D). Each repo integrates into its OWN origin under
    /// the shared per-turn branch name (distinct remotes → no collision), like the agents' per-repo push.
    /// </summary>
    private async Task<object> IntegrateMultiRepoAsync(SupervisorTurnContext context, IReadOnlyList<MergedAgent> merged, CancellationToken cancellationToken)
    {
        var repos = CollectWritableRepos(merged);

        // Each repo's PR base branch (the ref the agents — and therefore the integration — were rooted at, slice 4.1),
        // threaded into every per-repo block as `baseBranch` so the node output binds verbatim into git.open_change_set
        // as the per-repo PR target (S7-E). Computed ONCE off the merged agents' per-repo results, the single source.
        var baseByRepo = BaseBranchByRepo(merged);

        // A VERIFIED multi-repo resolution reconciled some repos into the resolver's OWN per-repo branches (S7-D2): for
        // THOSE repos, re-running the integrator over the original conflicting branches would just re-conflict, so we
        // surface the resolver's reconciled branch (per-repo short-circuit — the multi-repo analogue of S5's single
        // ResolutionIntegration). A repo NOT in this map (e.g. one that integrated cleanly the first time) re-integrates
        // normally, so the merge's repositories[] is the COMPLETE set: clean repos + resolved repos.
        var resolved = AcceptedResolutionRepositories(context);

        var blocks = new List<(string Status, object Block)>(repos.Count);

        foreach (var repo in repos)
            blocks.Add(resolved.TryGetValue(repo.RepositoryId, out var resolvedBranch)
                ? ("Clean", ResolvedRepoBlock(repo, resolvedBranch, baseByRepo.GetValueOrDefault(repo.RepositoryId, "")))
                : await IntegrateOneRepoAsync(repo, context, merged, baseByRepo.GetValueOrDefault(repo.RepositoryId, ""), cancellationToken).ConfigureAwait(false));

        // Honesty: per-repo work whose repository id is NULL (a degraded capture with no resolvable spec) can't be
        // cloned, so it's named as a Skipped block rather than silently dropped — an operator reading repositories[]
        // sees the uncombined work, mirroring the integrator's own "a dropped contribution is loudly named" invariant.
        blocks.AddRange(UnresolvableRepoBlocks(merged));

        var status = AggregateStatus(blocks.Select(b => b.Status).ToList());

        _logger.LogInformation("Supervisor integrated {RepoCount} repository axis(es) → {Status}", repos.Count, status);

        return new
        {
            status,
            reason = AggregateReason(blocks),
            repositories = blocks.Select(b => b.Block).ToList(),
        };
    }

    /// <summary>The writable repos to integrate, in first-seen spawn order, deduped by repository id — the union across the agents' per-repo results. A per-repo entry with a NULL repository id (a degraded capture with no resolvable spec) can't be cloned, so it is skipped here (its work stays on its branch); a repo every agent left null contributes nothing to integrate.</summary>
    private static IReadOnlyList<(Guid RepositoryId, string Alias)> CollectWritableRepos(IReadOnlyList<MergedAgent> merged)
    {
        var seen = new HashSet<Guid>();
        var repos = new List<(Guid, string)>();

        foreach (var agent in merged)
            foreach (var repo in agent.RepositoryResults)
                if (repo.RepositoryId is { } id && seen.Add(id))
                    repos.Add((id, repo.Alias));

        return repos;
    }

    /// <summary>Synthetic Skipped blocks for per-repo work whose repository id is NULL (a degraded capture) — grouped by alias, naming the agents whose work remains on their branches. A vacuous untouched null-id entry is dropped (nothing to combine); a real one is NAMED so the aggregate never silently omits uncombined work. Skipped (not Conflicted) — a missing spec is a degraded state, not a merge conflict.</summary>
    private static IEnumerable<(string Status, object Block)> UnresolvableRepoBlocks(IReadOnlyList<MergedAgent> merged) =>
        merged
            .SelectMany(agent => agent.RepositoryResults.Where(r => r.RepositoryId is null && !IsUntouched(r)).Select(r => (agent, r)))
            .GroupBy(x => x.r.Alias)
            .Select(g => ("Skipped", (object)new
            {
                repositoryId = (Guid?)null,
                alias = g.Key,
                status = "Skipped",
                reason = "no resolvable repository id for this repo — the agents' work remains on their branches",
                excludedAgents = g.Select(x => x.agent.AgentRunId.ToString()).Distinct().ToList(),
            }));

    /// <summary>Integrate ONE repo's per-repo contributions, returning its (status, block) pair. Mirrors the single-repo <see cref="IntegrateMergedAsync"/> fail-safe — unresolvable repo / no base → Skipped, git failure → Failed — scoped to this repo's per-repo patches + base. A repo an agent never TOUCHED (the capture layer still emits a vacuous entry) is dropped from the contributions so a disjoint fan-out doesn't spuriously conflict. <paramref name="baseBranch"/> is the repo's PR base (the ref it was rooted at), threaded onto the block so the node output binds verbatim into git.open_change_set (S7-E).</summary>
    private async Task<(string Status, object Block)> IntegrateOneRepoAsync((Guid RepositoryId, string Alias) repo, SupervisorTurnContext context, IReadOnlyList<MergedAgent> merged, string baseBranch, CancellationToken cancellationToken)
    {
        // This repo's REAL contributions: each agent's per-repo entry that ACTUALLY changed it. The capture layer emits
        // a RepositoryRunResult for EVERY writable repo — including one an agent never touched (empty patch, no branch).
        // A vacuous untouched entry is DROPPED (not integrated, NOT reported excluded — it isn't lost work): otherwise
        // the integrator refuses it as "no patch and no branch" and the common disjoint fan-out (agent A → web only,
        // agent B → api only) would spuriously abort each repo as Conflicted.
        var touched = merged
            .Select(agent => (agent, repoResult: agent.RepositoryResults.FirstOrDefault(r => r.RepositoryId == repo.RepositoryId)))
            .Where(x => x.repoResult is { } rr && !IsUntouched(rr))
            .Select(x => (x.agent, RepoResult: x.repoResult!))
            .ToList();

        var eligible = touched.Where(x => !string.IsNullOrEmpty(x.RepoResult.BaseSha)).ToList();
        var excluded = touched.Where(x => string.IsNullOrEmpty(x.RepoResult.BaseSha)).Select(x => x.agent.AgentRunId.ToString()).ToList();

        // The resolver THROWS for a deleted / cross-team / no-clone-URL repo (never returns null for those), so the
        // resolve is wrapped SEPARATELY → a Skipped block: an unresolvable repo must never abort the loop (sinking a
        // clean sibling) nor escape and strand the merge turn. A git failure DURING integrate is a distinct Failed.
        WorkspaceRequest? workspace;
        try
        {
            workspace = await _workspaces.ResolveByRepositoryIdAsync(repo.RepositoryId, context.TeamId, cancellationToken, string.IsNullOrEmpty(baseBranch) ? null : baseBranch).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            _logger.LogWarning(ex, "Supervisor could not resolve repository '{Alias}' to integrate; surfacing a Skipped block", repo.Alias);
            return ("Skipped", RepoSkipBlock(repo, "Skipped", ex.Message, excluded));
        }

        if (workspace is null) return ("Skipped", RepoSkipBlock(repo, "Skipped", "the repository could not be resolved to a clone target", excluded));

        if (eligible.Count == 0) return ("Skipped", RepoSkipBlock(repo, "Skipped", "no agent changed this repository with a recorded base revision", excluded));

        if (await EvaluatePublishGuardAsync(repo.RepositoryId, cancellationToken).ConfigureAwait(false) is { } guardVerdict)
            return ("Skipped", RepoSkipBlock(repo, "Skipped", $"publish policy: {guardVerdict.Reason}", excluded));

        var request = BuildPerRepoIntegrationRequest(repo.RepositoryId, context, workspace, eligible[0].RepoResult.BaseSha!, eligible);

        try
        {
            var result = await _integrator.IntegrateAsync(request, cancellationToken).ConfigureAwait(false);
            return (result.Status.ToString(), ProjectRepoBlock(repo, result, excluded, baseBranch));
        }
        catch (WorkspaceException ex)
        {
            _logger.LogWarning(ex, "Supervisor per-repo integration failed for '{Alias}'; keeping the side-by-side fold", repo.Alias);
            return ("Failed", RepoSkipBlock(repo, "Failed", ex.Message, excluded));
        }
    }

    /// <summary>Each repo's PR base branch — the ref the agents were rooted at (slice 4.1's <see cref="RepositoryRunResult.BaseBranch"/>), first-seen per repository id. The PR target a downstream git.open_change_set opens each per-repo PR into (S7-E). Empty entry for a repo whose agents recorded no base.</summary>
    private static IReadOnlyDictionary<Guid, string> BaseBranchByRepo(IReadOnlyList<MergedAgent> merged)
    {
        var map = new Dictionary<Guid, string>();

        foreach (var agent in merged)
            foreach (var repo in agent.RepositoryResults)
                if (repo.RepositoryId is { } id && !string.IsNullOrEmpty(repo.BaseBranch) && !map.ContainsKey(id))
                    map[id] = repo.BaseBranch!;

        return map;
    }

    /// <summary>A per-repo entry the agent never actually CHANGED — no diff (inline empty AND no offload ref) AND no pushed branch. The capture layer emits one <see cref="RepositoryRunResult"/> per writable repo even for an untouched one; integrating its vacuous "no patch, no branch" entry would make the integrator refuse the whole repo set, so it is dropped from the contributions — there is nothing to combine, and it is not lost work.</summary>
    private static bool IsUntouched(RepositoryRunResult repo) =>
        string.IsNullOrEmpty(repo.Patch) && repo.PatchArtifactId is null && string.IsNullOrEmpty(repo.ProducedBranch);

    private static IntegrationRequest BuildPerRepoIntegrationRequest(Guid repoId, SupervisorTurnContext context, WorkspaceRequest workspace, string baseSha, IReadOnlyList<(MergedAgent Agent, RepositoryRunResult RepoResult)> eligible) => new()
    {
        TeamId = context.TeamId,
        RepositoryUrl = workspace.RepositoryUrl,
        BaseRef = workspace.Ref,
        BaseSha = baseSha,
        Token = workspace.Token,
        TokenUsername = workspace.TokenUsername,
        IntegrationBranch = $"codespace/integration/{context.SupervisorRunId:N}/turn{context.TurnNumber}",
        Depth = 0,
        Contributions = eligible.Select(x => new BranchContribution
        {
            Label = x.Agent.AgentRunId.ToString(),
            SourceRepositoryId = repoId,
            BaseSha = x.RepoResult.BaseSha,
            Patch = x.RepoResult.Patch,            // already resolved in .Merge.cs (offloaded per-repo diffs folded back)
            ProducedBranch = x.RepoResult.ProducedBranch,
        }).ToList(),
    };

    /// <summary>One repo's integration block: the flat <see cref="ProjectIntegrationResult"/> fields PLUS its repository id + alias (the per-repo key the resolution loop acts on) + <c>baseBranch</c> (the PR target the node-output reader surfaces so a downstream git.open_change_set binds it — S7-E).</summary>
    private static object ProjectRepoBlock((Guid RepositoryId, string Alias) repo, IntegrationResult result, IReadOnlyList<string> excludedAgents, string baseBranch) => new
    {
        repositoryId = repo.RepositoryId,
        alias = repo.Alias,
        status = result.Status.ToString(),
        integratedBranch = result.IntegratedBranch,
        baseBranch,
        appliedCount = result.AppliedCount,
        reason = result.Reason,
        excludedAgents,
        outcomes = ProjectOutcomes(result),
    };

    /// <summary>One repo's NON-integrated block (Skipped / Failed) — no <see cref="IntegrationResult"/>, so no outcomes/branch, but still names the repo + why so the aggregate stays honest about what didn't combine.</summary>
    private static object RepoSkipBlock((Guid RepositoryId, string Alias) repo, string status, string reason, IReadOnlyList<string> excludedAgents) => new
    {
        repositoryId = repo.RepositoryId,
        alias = repo.Alias,
        status,
        reason,
        excludedAgents,
    };

    /// <summary>The worst-of aggregate over the per-repo statuses: <c>Conflicted</c> if ANY repo could not auto-combine (Conflicted / Failed / Partial), else <c>Clean</c> if at least one repo integrated cleanly, else <c>Skipped</c> (nothing to combine). Drives the decider's resolve-or-stop choice off the SAME ReadIntegration the single-repo path feeds.</summary>
    private static string AggregateStatus(IReadOnlyList<string> statuses)
    {
        if (statuses.Any(s => s is "Conflicted" or "Failed" or "Partial")) return "Conflicted";
        if (statuses.Any(s => s == "Clean")) return "Clean";
        return "Skipped";
    }

    /// <summary>A human-readable note on the multi-repo aggregate: which repos didn't auto-combine, or that all did. Null nothing-extra contract matches the single-repo <c>reason</c>.</summary>
    private static string? AggregateReason(IReadOnlyList<(string Status, object Block)> blocks)
    {
        var notClean = blocks.Where(b => b.Status is not ("Clean" or "Empty")).ToList();

        if (notClean.Count == 0) return blocks.Count == 0 ? "no resolvable repository among the per-repo results" : null;

        return $"{notClean.Count} of {blocks.Count} repositor{(blocks.Count == 1 ? "y" : "ies")} could not be auto-combined";
    }
}
