using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// The ASYNC resolve half of the real executor (Rule 10 <c>.Resolve.cs</c>, resolver loop #379): <c>resolve</c>
/// spawns ONE real <c>agent.code</c> run that reconciles a CONFLICTED integration's branches, builds, and runs the
/// tests — then parks on it (the K=1 spawn shape, reusing <c>StageAgentsAndParkAsync</c> verbatim). The resolver
/// task is assembled DETERMINISTICALLY from durable data (the conflicted merge's <c>integration</c> block + the
/// prior agents' produced branches) via <see cref="SupervisorResolverRecipe"/> — the decider only chose to attempt;
/// it never authored which branches or files, so a model mistake can't point the resolver at the wrong work.
///
/// <para>FAIL-SAFE: when there is nothing to resolve — no conflicted integration on the tape, no agent branches to
/// reconcile, or no resolvable repository — the verb is a SYNCHRONOUS no-op (the node self-advances, the decider
/// sees the skip and stops / does something else). The resolver loop only ever ADDS an attempt; it never strands
/// the run.</para>
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    private async Task<SupervisorExecution> ExecuteResolveAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        // Route by whether the most-recent conflicted merge is MULTI-repo (its integration carries a per-repo
        // repositories[] block), NOT by whether a repo is exactly "Conflicted": a multi-repo merge can aggregate to
        // Conflicted via a Failed/Skipped repo with no Conflicted block, and that must STILL take the multi-repo path
        // (where 0 reconcilable repos degrades to a no-op skip) rather than the single-repo flat path (which would
        // stage a flat resolver over only the primary repo's branches — wrong for a multi-repo run). (S7-D2)
        var conflictDecision = FindMostRecentConflictDecision(context);

        if (conflictDecision is not null && SupervisorOutcome.HasPerRepoIntegration(conflictDecision.OutcomeJson))
            return await ExecuteMultiRepoResolveAsync(context, cancellationToken).ConfigureAwait(false);

        var conflict = conflictDecision is null ? null : SupervisorOutcome.ReadIntegration(conflictDecision.OutcomeJson);
        var branches = CollectAgentBranches(context);

        var skip = ResolveSkipReason(conflict, branches, context);

        if (skip != null)
        {
            _logger.LogInformation("Supervisor resolve is a no-op at turn {Turn} ({Reason})", context.TurnNumber, skip);
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(new { resolve = "skipped", reason = skip }, AgentJson.Options));
        }

        var instruction = SupervisorResolverRecipe.BuildInstruction(context.Goal, conflict!, branches);

        var task = BuildTaskWithGoal(instruction, context, forcePushBranch: true);

        _logger.LogInformation("Supervisor staging a resolver agent at turn {Turn} to reconcile {Count} branch(es)", context.TurnNumber, branches.Count);

        return await StageAgentsAndParkAsync(new (AgentTask, SupervisorAgentDispatch?)[] { (task, null) }, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stage ONE multi-repo resolver (S7-D2): it runs in the profile's multi-repo workspace (the SAME Workspace
    /// <see cref="BuildTaskWithGoal"/> already projects) and reconciles EACH conflicted repo's full branch set in that
    /// repo's subdirectory. The resolver's per-repo pushed branches (its <c>RepositoryResults</c>) become the reconciled
    /// heads the supervisor accepts per repo. FAIL-SAFE: a no-resolvable-repo / no-branch / no-multi-repo-workspace state
    /// is a synchronous no-op (the loop only ever ADDS an attempt; it never strands the run).
    /// </summary>
    private async Task<SupervisorExecution> ExecuteMultiRepoResolveAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var sections = SupervisorOutcome.ReadConflictedRepos(context.PriorDecisions)
            .Select(r => new ResolverRepoSection { Alias = r.Alias, ConflictedFiles = r.ConflictedFiles, Branches = CollectAgentBranchesForRepo(context, r.RepositoryId) })
            .Where(s => s.Branches.Count > 0)
            .ToList();

        var skip = MultiRepoResolveSkipReason(sections, context);

        if (skip != null)
        {
            _logger.LogInformation("Supervisor multi-repo resolve is a no-op at turn {Turn} ({Reason})", context.TurnNumber, skip);
            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(new { resolve = "skipped", reason = skip }, AgentJson.Options));
        }

        var instruction = SupervisorResolverRecipe.BuildMultiRepoInstruction(context.Goal, sections);

        var task = BuildTaskWithGoal(instruction, context, forcePushBranch: true);

        _logger.LogInformation("Supervisor staging a multi-repo resolver agent at turn {Turn} to reconcile {Count} repositor(ies)", context.TurnNumber, sections.Count);

        return await StageAgentsAndParkAsync(new (AgentTask, SupervisorAgentDispatch?)[] { (task, null) }, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The non-null skip reason when a multi-repo resolution has nothing to do, else null. Distinct, legible fail-safes. The related-repos arm is defence-in-depth: a multi-repo conflict implies the run's profile HAD related repos (the spawn was multi-repo), but a misconfigured / replayed profile with none would project only a single-repo workspace whose per-repo subdirs the recipe references don't exist — so degrade to a no-op rather than stage a resolver into a workspace its instruction can't navigate.</summary>
    private static string? MultiRepoResolveSkipReason(IReadOnlyList<ResolverRepoSection> sections, SupervisorTurnContext context)
    {
        if (context.AgentProfile?.RepositoryId is null) return "no repository is bound to reconcile the branches in";
        if (AgentWorkspaceAuthoring.ParseRelatedRepositories(context.AgentProfile?.RelatedRepositories ?? default).Count == 0) return "the supervisor profile has no related repositories — there is no multi-repo workspace to reconcile in";
        if (sections.Count == 0) return "no agent branches were produced to reconcile in the conflicted repositories";
        return null;
    }

    /// <summary>
    /// EVERY produced branch the prior spawn/retry agents pushed FOR ONE REPO (by repository id), in spawn order, deduped
    /// — the FULL per-repo set the multi-repo resolver re-merges in that repo's subdirectory (the per-repo analogue of
    /// <see cref="CollectAgentBranches"/>, reading each agent's <c>RepositoryResults</c> entry for this repo). A null id
    /// or a branch-less per-repo entry contributes nothing.
    /// </summary>
    internal static IReadOnlyList<string> CollectAgentBranchesForRepo(SupervisorTurnContext context, Guid? repositoryId)
    {
        if (repositoryId is null) return Array.Empty<string>();

        var branches = new List<string>();

        foreach (var prior in context.PriorDecisions.Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry))
            foreach (var result in SupervisorOutcome.ReadAgentResults(prior.OutcomeJson))
            {
                if (SupervisorOutcome.IsAcceptanceRejected(result)) continue;   // slice 4: a per-unit-rejected unit's work never reaches the head, via the resolver door either

                foreach (var repo in result.RepositoryResults)
                    if (repo.RepositoryId == repositoryId && !string.IsNullOrWhiteSpace(repo.ProducedBranch) && !branches.Contains(repo.ProducedBranch!))
                        branches.Add(repo.ProducedBranch!);
            }

        return branches;
    }

    /// <summary>The non-null skip reason when there is nothing to resolve, else null (proceed). Each arm is a distinct, legible fail-safe so the no-op outcome names WHY the resolver didn't run.</summary>
    private static string? ResolveSkipReason(SupervisorIntegrationOutcome? conflict, IReadOnlyList<string> branches, SupervisorTurnContext context)
    {
        if (conflict is null) return "no conflicted integration to resolve";
        if (context.AgentProfile?.RepositoryId is null) return "no repository is bound to reconcile the branches in";
        if (branches.Count == 0) return "no agent branches were produced to reconcile";
        return null;
    }

    /// <summary>
    /// The MOST RECENT prior <c>merge</c> OR <c>spawn</c> DECISION whose recorded integration CONFLICTED (the freshest
    /// conflict the resolver should act on), or null when none conflicted. Walks newest-first. A <c>spawn</c> conflicts
    /// when its S1 dependency staging could not auto-integrate its producers onto one branch (<c>.DependencyStaging.cs</c>
    /// / <c>.Spawn.cs</c>'s <c>BuildBlockedSpawnOutcome</c>) — recorded in the SAME <c>integration</c> shape a <c>merge</c>
    /// records, so this ONE reader routes both without a second escalation mechanism. Returns the decision (not just the
    /// parsed outcome) so the caller can both read its <see cref="SupervisorIntegrationOutcome"/> AND inspect its raw
    /// shape (single- vs multi-repo) to route resolution. Internal + static so the widened Merge-OR-Spawn kind check is
    /// unit-pinned directly — no other decision kind may ever be misread as a conflict source.
    /// </summary>
    internal static SupervisorPriorDecision? FindMostRecentConflictDecision(SupervisorTurnContext context)
    {
        for (var i = context.PriorDecisions.Count - 1; i >= 0; i--)
        {
            var prior = context.PriorDecisions[i];

            if (prior.DecisionKind != SupervisorDecisionKinds.Merge && prior.DecisionKind != SupervisorDecisionKinds.Spawn) continue;

            if (SupervisorOutcome.ReadIntegration(prior.OutcomeJson) is { IsConflicted: true }) return prior;
        }

        return null;
    }

    /// <summary>
    /// EVERY produced branch the prior spawn/retry agents pushed, in spawn order, deduped — the FULL set the resolver
    /// re-merges (NOT just the conflicting subset the integration block names; the resolver needs all the agents'
    /// branches to reconcile them) MINUS any unit a per-unit acceptance grade objectively REJECTED (slice 4 — withheld
    /// via <see cref="SupervisorOutcome.IsAcceptanceRejected"/>). Mirrors <see cref="ResolveAgentRunIdsToMerge"/>'s "all
    /// prior spawn/retry minus rejected" scope so the resolver reconciles exactly the set the merge integrated (the two
    /// doors to the head withhold the same units). A branch-less agent (failed / no push) contributes nothing.
    /// </summary>
    internal static IReadOnlyList<string> CollectAgentBranches(SupervisorTurnContext context)
    {
        var branches = new List<string>();

        foreach (var prior in context.PriorDecisions.Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry))
            foreach (var result in SupervisorOutcome.ReadAgentResults(prior.OutcomeJson))
                if (!SupervisorOutcome.IsAcceptanceRejected(result) && !string.IsNullOrWhiteSpace(result.ProducedBranch) && !branches.Contains(result.ProducedBranch!))
                    branches.Add(result.ProducedBranch!);

        return branches;
    }
}
