using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The supervisor lane's <see cref="ExecutableSet"/> computation (P1b) — from the tape's plan decisions alone,
/// decision-bound like everything else in P1: the CURRENT set is the LATEST ref-bearing plan decision's units
/// (payload subtasks + the outcome's recorded plan row, #1209), diffed against the previous ref-bearing plan's
/// units by PLAN-GRAIN contract hash: same id + same hash → <see cref="UnitDisposition.Carried"/>, same id +
/// different hash → <see cref="UnitDisposition.Replaced"/>, id absent before → <see cref="UnitDisposition.New"/>,
/// id absent now → cancelled (diagnostics, never a member). Null when the tape has no ref-bearing plan (a legacy
/// or plan-less run — Shadow/Legacy handle it; an Enforced plan-less lane uses <see cref="ExecutableSet.SyntheticRoot"/>).
/// Boundary (documented, closed at P2a): the latest ref-bearing plan DECISION defines the set — the composer
/// cross-checks the plan ROW's confirmation status before an assessment leans on it, because a human-rejected
/// re-plan can sit latest on the tape while spawns stay gated.
/// </summary>
public static class SupervisorExecutableSet
{
    public static ExecutableSet? Compute(IReadOnlyList<SupervisorPriorDecision> decisions)
    {
        var plans = decisions
            .Where(d => d.DecisionKind == SupervisorDecisionKinds.Plan)
            .OrderBy(d => d.Sequence)
            .Select(d => (Ref: SupervisorOutcome.ReadPlanRef(d.OutcomeJson), Decision: d))
            .Where(p => p.Ref is not null)
            .ToList();

        if (plans.Count == 0) return null;

        var current = plans[^1];
        var currentHashes = PlanGrainHashes(current.Decision);
        var priorHashes = plans.Count > 1 ? PlanGrainHashes(plans[^2].Decision) : null;

        var units = currentHashes.Select(kvp => new ExecutableUnit
        {
            UnitId = kvp.Key,
            ContractHash = kvp.Value,
            Disposition = priorHashes is null || !priorHashes.TryGetValue(kvp.Key, out var priorHash) ? UnitDisposition.New
                : priorHash == kvp.Value ? UnitDisposition.Carried
                : UnitDisposition.Replaced,
        }).ToList();

        var cancelled = priorHashes is null
            ? Array.Empty<string>()
            : priorHashes.Keys.Where(id => !currentHashes.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        return ExecutableSet.Create(current.Ref!.Value.WorkPlanId, current.Ref.Value.Version, units, cancelled);
    }

    /// <summary>Each planned unit's PLAN-GRAIN contract hash (no dispatch overrides — those are attempt-grain). First occurrence wins on a duplicate id, mirroring the acceptance resolver's precedent.</summary>
    private static Dictionary<string, string> PlanGrainHashes(SupervisorPriorDecision planDecision)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var subtask in SupervisorOutcome.ReadPlanSubtasks(planDecision.PayloadJson))
            if (!hashes.ContainsKey(subtask.Id))
                hashes[subtask.Id] = SupervisorUnitContract.Hash(subtask, effectiveInstruction: null, repositoryOverride: null);

        return hashes;
    }
}
