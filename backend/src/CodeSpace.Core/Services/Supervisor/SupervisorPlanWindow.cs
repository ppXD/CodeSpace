using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The PURE generation boundary for supervisor tape consumers: the latest successfully-recorded, structurally-valid,
/// non-empty Plan and everything after it. Earlier attempts belong to a superseded plan generation and can remain on
/// the append-only tape for audit, but cannot satisfy dependencies or become the current reviewable head. Empty,
/// malformed, structurally-invalid, and failed Plan records do not open a generation. A plan-less legacy tape is
/// returned verbatim, preserving its pre-window behavior and allocation-free fast path.
/// </summary>
public static class SupervisorPlanWindow
{
    public static SupervisorPlanWindowSlice Read(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        ArgumentNullException.ThrowIfNull(priorDecisions);

        for (var i = priorDecisions.Count - 1; i >= 0; i--)
        {
            var candidate = priorDecisions[i];
            if (!IsValidBoundary(candidate)) continue;

            return new SupervisorPlanWindowSlice(i == 0 ? priorDecisions : priorDecisions.Skip(i).ToArray(), IsPlanBounded: true);
        }

        return new SupervisorPlanWindowSlice(priorDecisions, IsPlanBounded: false);
    }

    private static bool IsValidBoundary(SupervisorPriorDecision candidate)
    {
        if (candidate.DecisionKind != SupervisorDecisionKinds.Plan || candidate.Status != SupervisorDecisionStatus.Succeeded) return false;
        if (SupervisorOutcome.ReadPlanSubtasks(candidate.PayloadJson).Count == 0) return false;

        return SupervisorPlanValidator.Validate(new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = candidate.PayloadJson }) is null;
    }
}

public sealed record SupervisorPlanWindowSlice(IReadOnlyList<SupervisorPriorDecision> Decisions, bool IsPlanBounded);
