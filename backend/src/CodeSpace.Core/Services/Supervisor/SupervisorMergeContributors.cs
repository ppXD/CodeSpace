using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// WHICH agent runs a <c>merge</c> folds — the ONE function the merge executor's door and the decider's plan
/// recitation both read, so the prompt can never tell the brain something the merge would not actually do.
///
/// <para>CONSERVATION across a plan-generation boundary: the active generation (<see cref="SupervisorPlanWindow"/>)
/// is authoritative whenever it staged ANYTHING, so the ordinary run is untouched. But a re-plan issued AFTER a wave
/// finished slices the window past every spawn that produced it, and a merge scoped strictly to that window folds
/// zero results — a live run pushed three accepted agent branches, re-planned three times, then merged 0 and
/// published 0 targets. A plan-generation boundary may supersede an INSTRUCTION; it must not make FINISHED work
/// invisible. So a window that yields nothing falls back to the run's earlier Succeeded, not-withheld,
/// not-yet-merged agent runs — read off the same append-only tape (prior <c>merge</c> outcomes say what is already
/// consolidated), never a second source of truth.</para>
/// </summary>
public static class SupervisorMergeContributors
{
    /// <summary>The contributors this turn's <c>merge</c> would fold, and how many of them a plan-generation boundary had stranded.</summary>
    public static SupervisorMergeContributorSelection Resolve(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        ArgumentNullException.ThrowIfNull(priorDecisions);

        var active = ActiveGeneration(priorDecisions);

        if (active.Count > 0) return new SupervisorMergeContributorSelection(active, 0);

        var carriedOver = StrandedByAReplan(priorDecisions);

        return new SupervisorMergeContributorSelection(carriedOver, carriedOver.Count);
    }

    /// <summary>
    /// The agent-run ids recorded by EVERY spawn/retry decision in the active <see cref="SupervisorPlanWindow"/>
    /// generation (in order) MINUS any unit a per-unit acceptance grade objectively REJECTED or a human WAIVED
    /// (loopability slice 4, "局部綠≠整合綠"): a unit that failed its OWN definition-of-done must not be integrated
    /// into the reviewable head, even if the model merges. A unit with NO verdict integrates exactly as before.
    /// </summary>
    private static IReadOnlyList<Guid> ActiveGeneration(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var staging = SupervisorPlanWindow.Read(priorDecisions).Decisions
            .Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)
            .ToList();

        var rejected = staging
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .Where(SupervisorOutcome.IsWithheldFromHead)
            .Select(r => r.AgentRunId)
            .ToHashSet();

        return staging
            .SelectMany(d => SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson))
            .Where(id => !rejected.Contains(id))
            .ToList();
    }

    /// <summary>
    /// Every agent run this supervisor run FINISHED and nobody has merged, when the active generation staged nothing
    /// mergeable of its own. Deliberately narrower than <see cref="ActiveGeneration"/>: that path stages ids
    /// regardless of status (a still-running wave is the generation's own work), while a carry-over only ever
    /// conserves SETTLED work — Succeeded, past the same <see cref="SupervisorOutcome.IsWithheldFromHead"/> door
    /// (a rejected or waived unit is no more mergeable here than it was there), and not already folded by an earlier
    /// <c>merge</c> outcome. Tape order, so the fold sees them in the order they were produced.
    /// </summary>
    private static IReadOnlyList<Guid> StrandedByAReplan(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var alreadyMerged = priorDecisions
            .Where(d => d.DecisionKind == SupervisorDecisionKinds.Merge)
            .SelectMany(d => SupervisorOutcome.ReadMergedAgentRunIds(d.OutcomeJson))
            .ToHashSet();

        return priorDecisions
            .Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .Where(r => r.Status == nameof(AgentRunStatus.Succeeded) && !SupervisorOutcome.IsWithheldFromHead(r) && !alreadyMerged.Contains(r.AgentRunId))
            .Select(r => r.AgentRunId)
            .Distinct()
            .ToList();
    }
}

/// <summary>The contributors one <c>merge</c> folds, plus how many of them a plan-generation boundary would otherwise have stranded. A pure read of the tape.</summary>
public sealed record SupervisorMergeContributorSelection(IReadOnlyList<Guid> AgentRunIds, int CarriedOverFromEarlierGenerations);
