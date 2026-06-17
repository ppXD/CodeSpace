using System.Text.Json;
using CodeSpace.Core.Services.Agents;
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
        var conflict = FindMostRecentConflict(context);
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

        return await StageAgentsAndParkAsync(new[] { task }, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The non-null skip reason when there is nothing to resolve, else null (proceed). Each arm is a distinct, legible fail-safe so the no-op outcome names WHY the resolver didn't run.</summary>
    private static string? ResolveSkipReason(SupervisorIntegrationOutcome? conflict, IReadOnlyList<string> branches, SupervisorTurnContext context)
    {
        if (conflict is null) return "no conflicted integration to resolve";
        if (context.AgentProfile?.RepositoryId is null) return "no repository is bound to reconcile the branches in";
        if (branches.Count == 0) return "no agent branches were produced to reconcile";
        return null;
    }

    /// <summary>The MOST RECENT prior <c>merge</c> decision whose recorded integration CONFLICTED (the freshest conflict the resolver should act on), or null when no prior merge conflicted. Walks newest-first.</summary>
    private static SupervisorIntegrationOutcome? FindMostRecentConflict(SupervisorTurnContext context)
    {
        for (var i = context.PriorDecisions.Count - 1; i >= 0; i--)
        {
            var prior = context.PriorDecisions[i];

            if (prior.DecisionKind != SupervisorDecisionKinds.Merge) continue;

            var integration = SupervisorOutcome.ReadIntegration(prior.OutcomeJson);

            if (integration is { IsConflicted: true }) return integration;
        }

        return null;
    }

    /// <summary>
    /// EVERY produced branch the prior spawn/retry agents pushed, in spawn order, deduped — the FULL set the resolver
    /// re-merges (NOT just the conflicting subset the integration block names; the resolver needs all the agents'
    /// branches to reconcile them). Mirrors <c>ResolveAgentRunIdsToMerge</c>'s "all prior spawn/retry" scope so the
    /// resolver reconciles exactly the set the merge tried to integrate. A branch-less agent (failed / no push)
    /// contributes nothing.
    /// </summary>
    private static IReadOnlyList<string> CollectAgentBranches(SupervisorTurnContext context)
    {
        var branches = new List<string>();

        foreach (var prior in context.PriorDecisions.Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry))
            foreach (var result in SupervisorOutcome.ReadAgentResults(prior.OutcomeJson))
                if (!string.IsNullOrWhiteSpace(result.ProducedBranch) && !branches.Contains(result.ProducedBranch!))
                    branches.Add(result.ProducedBranch!);

        return branches;
    }
}
