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
///
/// <para>DC-3's ledger-direct publish rung (<see cref="SupervisorPublishedBranchResolver"/>) has the identical blind
/// spot on its own rung and reads the same floor — <see cref="SettledAcrossGenerations"/>. See its remarks for why
/// "already merged" is an exclusion here and not there.</para>
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
    /// mergeable of its own — <see cref="SettledAcrossGenerations"/> minus what an earlier <c>merge</c> outcome
    /// already consolidated (this rung's own runaway backstop: re-folding the same ids must not read as new progress).
    /// </summary>
    private static IReadOnlyList<Guid> StrandedByAReplan(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var alreadyMerged = priorDecisions
            .Where(d => d.DecisionKind == SupervisorDecisionKinds.Merge)
            .SelectMany(d => SupervisorOutcome.ReadMergedAgentRunIds(d.OutcomeJson))
            .ToHashSet();

        return SettledAcrossGenerations(priorDecisions).Where(id => !alreadyMerged.Contains(id)).ToList();
    }

    /// <summary>
    /// The whole tape's SETTLED work, in the order it was produced — the floor BOTH carry-over rungs read, so the
    /// merge and the publish resolver cannot drift on what a plan-generation boundary is allowed to hide.
    /// Deliberately narrower than <see cref="ActiveGeneration"/>: that path stages ids regardless of status (a
    /// still-running wave is the generation's own work), while a carry-over only ever conserves finished work —
    /// Succeeded, past the same <see cref="SupervisorOutcome.IsWithheldFromHead"/> door (a rejected or waived unit
    /// is no more publishable here than it was mergeable there).
    ///
    /// <para>Says nothing about what a prior <c>merge</c> already folded — that exclusion belongs to the merge rung
    /// alone (<see cref="StrandedByAReplan"/>). DC-3's ledger-direct publish rung must NOT inherit it: that rung only
    /// runs when no merge produced an integrated branch at all (gate off, conflicted, patch-only), and there a
    /// contributor's own pushed branch is still the only genuinely published artifact — which is also how that
    /// resolver treats merged contributors today, since it reads the manifest ledger and never consults a merge
    /// outcome.</para>
    /// </summary>
    public static IReadOnlyList<Guid> SettledAcrossGenerations(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        ArgumentNullException.ThrowIfNull(priorDecisions);

        return priorDecisions
            .Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .Where(r => r.Status == nameof(AgentRunStatus.Succeeded) && !SupervisorOutcome.IsWithheldFromHead(r))
            .Select(r => r.AgentRunId)
            .Distinct()
            .ToList();
    }
}

/// <summary>The contributors one <c>merge</c> folds, plus how many of them a plan-generation boundary would otherwise have stranded. A pure read of the tape.</summary>
public sealed record SupervisorMergeContributorSelection(IReadOnlyList<Guid> AgentRunIds, int CarriedOverFromEarlierGenerations);
