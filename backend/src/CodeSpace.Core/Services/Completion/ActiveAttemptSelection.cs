using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// THE one active-attempt selector (P1 identity): which attempt's evidence counts for each unit. Latest staged
/// attempt wins — walk the tape's TERMINAL spawn/retry decisions in ledger order; for every subtask a later
/// decision staged, the later attempt's result REPLACES the earlier one, so a retried-then-passed unit reads its
/// passing attempt and the superseded failure's evidence never reaches a fold. This is the rule's single home:
/// the completion composer reads it now, and P+'s CurrentExecutableSet composes with it later — two independent
/// implementations of "which attempt counts" would diverge exactly where it matters (the scorecard-shifting
/// class the B-pre withdrawal documented). The kernel deliberately does NOT re-implement this: the reducer folds
/// every receipt it is handed worst-first, so the composer MUST pre-filter through this selector.
/// </summary>
public static class ActiveAttemptSelection
{
    /// <summary>The active (latest staged, terminal) attempt per subtask id. A non-terminal decision contributes nothing (its results are not yet durable facts); a retry with no subtask id replaces nothing.</summary>
    public static IReadOnlyDictionary<string, SupervisorAgentResult> SelectActive(IReadOnlyList<SupervisorPriorDecision> decisions)
    {
        var active = new Dictionary<string, SupervisorAgentResult>(StringComparer.Ordinal);

        foreach (var decision in decisions.OrderBy(d => d.Sequence))
        {
            if (decision.DecisionKind is not (SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)) continue;

            if (!SupervisorDecisionStateMachine.IsTerminal(decision.Status)) continue;

            var subtaskIds = SubtaskIdsFor(decision);
            var results = SupervisorOutcome.ReadAgentResults(decision.OutcomeJson);

            // The positional join the folds ride: results[i] ran subtaskIds[i], the order the executor staged them.
            for (var i = 0; i < results.Count && i < subtaskIds.Count; i++)
                if (!string.IsNullOrEmpty(subtaskIds[i]))
                    active[subtaskIds[i]] = results[i];
        }

        return active;
    }

    private static IReadOnlyList<string> SubtaskIdsFor(SupervisorPriorDecision decision) =>
        decision.DecisionKind == SupervisorDecisionKinds.Spawn
            ? SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson)
            : SupervisorOutcome.ReadRetrySubtaskId(decision.PayloadJson) is { } id ? new[] { id } : Array.Empty<string>();
}
