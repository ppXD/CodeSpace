using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>The supervisor adapter's projection result: the attempts plus every contract-integrity violation met while projecting. Errors are surfaced per policy mode downstream (Shadow → ContractError display; Enforced → Unknown/Park) — NEVER silently truncated away (Lock Clause 3).</summary>
public sealed record SupervisorAttemptProjectionSet(IReadOnlyList<AttemptProjection> Attempts, IReadOnlyList<string> ContractErrors);

/// <summary>
/// The SUPERVISOR lane's adapter onto the generic <see cref="AttemptProjection"/> vocabulary (P1b) — walks the
/// tape's staging decisions in ledger order and emits one projection per staged attempt. A TERMINAL spawn/retry
/// contributes <see cref="AttemptState.Settled"/> attempts (its folded results are durable facts); a NON-terminal
/// staging decision contributes <see cref="AttemptState.Authorized"/> attempts (staged agent ids, evidence not yet
/// durable) — the operational selector must see them so a superseded Passed attempt can never terminalize while a
/// newer authorized attempt is in flight (Lock Clause 3). Plan identity comes from each staging decision's LATEST
/// PRECEDING plan decision's own recorded ref (decision-bound, #1209); the optional
/// <paramref name="workUnitsByAttempt"/> lookup (from staged task rows) enriches with the dispatch-time stamp —
/// including ContractHash — and WINS over tape reconstruction when present. Positional mismatches between a
/// decision's unit list and its results/staged ids are reported as contract errors, never silently truncated.
/// This adapter and the two <see cref="AttemptSelectors"/> are the ONLY attempt-rule authorities — the retired
/// per-lane selector (#1207's ActiveAttemptSelection) must never be reintroduced beside them.
/// </summary>
public static class SupervisorAttemptAdapter
{
    public static SupervisorAttemptProjectionSet Project(IReadOnlyList<SupervisorPriorDecision> decisions, IReadOnlyDictionary<Guid, WorkUnitRef>? workUnitsByAttempt = null)
    {
        var attempts = new List<AttemptProjection>();
        var errors = new List<string>();
        var ordinals = new Dictionary<UnitKey, int>();
        (Guid WorkPlanId, int Version)? planRef = null;

        foreach (var decision in decisions.OrderBy(d => d.Sequence))
        {
            if (decision.DecisionKind == SupervisorDecisionKinds.Plan)
            {
                planRef = SupervisorOutcome.ReadPlanRef(decision.OutcomeJson) ?? planRef;
                continue;
            }

            if (decision.DecisionKind is not (SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)) continue;

            var unitIds = UnitIdsFor(decision);

            if (unitIds.Count == 0) continue;

            var settled = SupervisorDecisionStateMachine.IsTerminal(decision.Status);
            var attemptIds = settled
                ? SupervisorOutcome.ReadAgentResults(decision.OutcomeJson).Select(r => r.AgentRunId).ToList()
                : SupervisorOutcome.ReadStagedAgentRunIds(decision.OutcomeJson).ToList();

            if (attemptIds.Count == 0) continue;   // authorized-but-never-staged (crash before staging) — no attempt exists

            if (attemptIds.Count != unitIds.Count)
                errors.Add($"decision {decision.Id} ({decision.DecisionKind}, seq {decision.Sequence}): {unitIds.Count} unit(s) but {attemptIds.Count} attempt id(s) — positional contract broken");

            for (var i = 0; i < Math.Min(attemptIds.Count, unitIds.Count); i++)
            {
                if (string.IsNullOrEmpty(unitIds[i])) continue;

                var workUnit = workUnitsByAttempt?.GetValueOrDefault(attemptIds[i])
                               ?? (planRef is { } plan ? new WorkUnitRef { WorkPlanId = plan.WorkPlanId, PlanVersion = plan.Version, UnitId = unitIds[i] } : null);

                var draft = new AttemptProjection
                {
                    AttemptId = attemptIds[i],
                    UnitId = unitIds[i],
                    WorkUnit = workUnit,
                    AttemptOrdinal = 0,
                    State = settled ? AttemptState.Settled : AttemptState.Authorized,
                };

                var key = UnitKey.For(draft);
                var ordinal = ordinals.GetValueOrDefault(key) + 1;
                ordinals[key] = ordinal;

                attempts.Add(draft with { AttemptOrdinal = ordinal });
            }
        }

        return new SupervisorAttemptProjectionSet(attempts, errors);
    }

    private static IReadOnlyList<string> UnitIdsFor(SupervisorPriorDecision decision) =>
        decision.DecisionKind == SupervisorDecisionKinds.Spawn
            ? SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson)
            : SupervisorOutcome.ReadRetrySubtaskId(decision.PayloadJson) is { } id ? new[] { id } : Array.Empty<string>();
}
