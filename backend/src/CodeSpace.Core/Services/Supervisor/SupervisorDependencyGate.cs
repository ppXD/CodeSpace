using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The PURE dependency-ordering rail (loopability — the build-graph's "explicit dependency" made executable; no DB, no
/// state, the sibling of <see cref="SupervisorBounds"/>). The model authors a plan's <c>DependsOn</c> edges (a DAG);
/// the SERVER enforces them at spawn time — a subtask runs only once EVERY dependency is SATISFIED. The model proposes
/// a spawn set; <see cref="Partition"/> admits the ready subset and defers the rest, so a unit can build on a versioned,
/// accepted predecessor instead of racing it.
///
/// <para>"Satisfied" = a dependency an agent ran to a non-rejected success: its latest attempt is <c>Succeeded</c> AND
/// not objectively rejected by its per-unit acceptance (<see cref="SupervisorAgentResult.AcceptancePassed"/> != false).
/// A dependency that FAILED its acceptance is NOT a usable contract, so its dependents stay blocked until a retry of it
/// succeeds. A plan with NO <c>DependsOn</c> (the flat-plan default) admits every requested subtask verbatim —
/// byte-identical to before. A cyclic / dangling DAG never satisfies, so its dependents never become ready and the run
/// converges to the no-progress bound (a clean stop) rather than looping; a dedicated plan validator can fail it faster.</para>
/// </summary>
public static class SupervisorDependencyGate
{
    /// <summary>
    /// Partition a spawn's REQUESTED subtask ids into those READY to run (every <c>DependsOn</c> satisfied) and those
    /// DEFERRED (a dependency is not yet a non-rejected success). Order-preserving. A plan with no dependency edges →
    /// <c>(requested, [])</c> verbatim (the byte-identical fast path). The server clamps the spawn to <c>Ready</c>; the
    /// model re-proposes the deferred ones on a later turn once their dependencies settle.
    /// </summary>
    public static (IReadOnlyList<string> Ready, IReadOnlyList<string> Deferred) Partition(SupervisorTurnContext context, IReadOnlyList<string> requestedSubtaskIds)
    {
        var dependsOn = DependsOnBySubtask(context);

        if (dependsOn.Count == 0) return (requestedSubtaskIds, Array.Empty<string>());   // no DAG → every subtask is ready (byte-identical to pre-slice)

        var satisfied = SatisfiedSubtaskIds(context);

        var ready = new List<string>();
        var deferred = new List<string>();

        foreach (var id in requestedSubtaskIds)
        {
            if (dependsOn.TryGetValue(id, out var deps) && deps.Any(dep => !satisfied.Contains(dep)))
                deferred.Add(id);
            else
                ready.Add(id);
        }

        return (ready, deferred);
    }

    /// <summary>
    /// The plan's dependency FRONTIER for the decider prompt: the planned subtasks that are READY to spawn now (not yet
    /// done, every dependency satisfied) and those still BLOCKED (with the unmet dependencies they wait on). Empty for a
    /// flat plan (no DAG → nothing to render). Lets the model spawn in dependency order instead of racing — the guidance
    /// half of the rail the clamp enforces.
    /// </summary>
    public static (IReadOnlyList<string> Ready, IReadOnlyList<BlockedSubtask> Blocked) Frontier(SupervisorTurnContext context)
    {
        var dependsOn = DependsOnBySubtask(context);

        if (dependsOn.Count == 0) return (Array.Empty<string>(), Array.Empty<BlockedSubtask>());

        var satisfied = SatisfiedSubtaskIds(context);

        var ready = new List<string>();
        var blocked = new List<BlockedSubtask>();

        foreach (var id in AllPlannedSubtaskIds(context))
        {
            if (satisfied.Contains(id)) continue;   // already done — not part of the frontier

            var unmet = dependsOn.TryGetValue(id, out var deps) ? deps.Where(dep => !satisfied.Contains(dep)).ToList() : new List<string>();

            if (unmet.Count == 0) ready.Add(id);
            else blocked.Add(new BlockedSubtask(id, unmet));
        }

        return (ready, blocked);
    }

    /// <summary>One blocked planned subtask + the dependency ids it is still waiting on (for the decider's frontier rendering).</summary>
    public readonly record struct BlockedSubtask(string Id, IReadOnlyList<string> WaitingOn);

    /// <summary>
    /// The subtask ids whose LATEST attempt is a non-rejected success — the dependencies a dependent may build on. Reads
    /// every prior spawn/retry positionally (<c>subtaskIds[i] ↔ agentResults[i]</c>, the ordering-safe join), LAST attempt
    /// wins (a retry supersedes the original), and a unit counts only when <c>Succeeded</c> AND not acceptance-rejected.
    /// </summary>
    private static IReadOnlySet<string> SatisfiedSubtaskIds(SupervisorTurnContext context) =>
        LatestResultsBySubtask(context)
            .Where(kv => IsSatisfied(kv.Value))
            .Select(kv => kv.Key)
            .ToHashSet();

    /// <summary>
    /// The AgentRunIds of a dependent subtask's PRODUCERS (S1 handoff) — each requested dependency id's LATEST
    /// attempt, but ONLY when that attempt is a non-rejected success (the SAME "satisfied" test <see cref="SatisfiedSubtaskIds"/>
    /// uses, so a handoff never reads from a dependency the spawn clamp itself would not have admitted on). A dependency
    /// with no recorded result, or whose latest attempt did not succeed, contributes nothing — defensive; <see cref="Partition"/>
    /// should already have deferred such a subtask before it reaches staging. Order-preserving over <paramref name="dependsOn"/>.
    /// </summary>
    public static IReadOnlyList<Guid> LatestSucceededAgentRunIds(SupervisorTurnContext context, IReadOnlyList<string> dependsOn)
    {
        var latest = LatestResultsBySubtask(context);

        return dependsOn
            .Select(dep => latest.TryGetValue(dep, out var result) && IsSatisfied(result) ? result.AgentRunId : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }

    /// <summary>A dependency counts as satisfied iff its latest attempt SUCCEEDED and was not objectively acceptance-rejected — the single definition <see cref="SatisfiedSubtaskIds"/> and <see cref="LatestSucceededAgentRunIds"/> share so they can never drift.</summary>
    private static bool IsSatisfied(SupervisorAgentResult result) =>
        string.Equals(result.Status, nameof(AgentRunStatus.Succeeded), StringComparison.Ordinal) && result.AcceptancePassed != false;

    /// <summary>Every planned subtask id's LATEST folded result (a retry's result supersedes its original), read positionally off every prior spawn/retry/resolve decision (<c>subtaskIds[i] ↔ agentResults[i]</c>) — the shared walk <see cref="SatisfiedSubtaskIds"/> and <see cref="LatestSucceededAgentRunIds"/> both derive from.</summary>
    private static IReadOnlyDictionary<string, SupervisorAgentResult> LatestResultsBySubtask(SupervisorTurnContext context)
    {
        var latest = new Dictionary<string, SupervisorAgentResult>();

        foreach (var prior in context.PriorDecisions.Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind)))
        {
            var ids = SubtaskIdsOf(prior);
            var results = SupervisorOutcome.ReadAgentResults(prior.OutcomeJson);

            for (var i = 0; i < ids.Count && i < results.Count; i++)
                latest[ids[i]] = results[i];
        }

        return latest;
    }

    /// <summary>The plan-local subtask ids a spawn (positional fan-out) or retry (one) ran — the positional join key to its folded <c>agentResults</c>.</summary>
    private static IReadOnlyList<string> SubtaskIdsOf(SupervisorPriorDecision decision) =>
        decision.DecisionKind == SupervisorDecisionKinds.Spawn
            ? SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson)
            : SupervisorOutcome.ReadRetrySubtaskId(decision.PayloadJson) is { } id ? new[] { id } : Array.Empty<string>();

    /// <summary>The most recent plan's <c>DependsOn</c> edges, keyed by subtask id — only subtasks that DECLARE a dependency are included (an empty map ⇒ a flat plan ⇒ the byte-identical fast path). Duplicate ids keep the first.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> DependsOnBySubtask(SupervisorTurnContext context)
    {
        for (var i = context.PriorDecisions.Count - 1; i >= 0; i--)
        {
            if (context.PriorDecisions[i].DecisionKind != SupervisorDecisionKinds.Plan) continue;

            var map = new Dictionary<string, IReadOnlyList<string>>();

            foreach (var subtask in SupervisorOutcome.ReadPlanSubtasks(context.PriorDecisions[i].PayloadJson))
                if (subtask.DependsOn is { Count: > 0 } deps && !map.ContainsKey(subtask.Id))
                    map[subtask.Id] = deps;

            return map;
        }

        return EmptyDependsOn;
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyDependsOn = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Every planned subtask id off the most recent plan (in plan order) — the universe the <see cref="Frontier"/> partitions. Empty when no plan was recorded.</summary>
    private static IReadOnlyList<string> AllPlannedSubtaskIds(SupervisorTurnContext context)
    {
        for (var i = context.PriorDecisions.Count - 1; i >= 0; i--)
            if (context.PriorDecisions[i].DecisionKind == SupervisorDecisionKinds.Plan)
                return SupervisorOutcome.ReadPlanSubtasks(context.PriorDecisions[i].PayloadJson).Select(s => s.Id).ToList();

        return Array.Empty<string>();
    }
}
