using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// WHICH agent runs a <c>merge</c> folds — the ONE function the merge executor's door and the decider's plan
/// recitation both read, so the prompt can never tell the brain something the merge would not actually do.
///
/// <para>CONSERVATION across a plan-generation boundary: the active generation (<see cref="SupervisorPlanWindow"/>)
/// is authoritative whenever it has a mergeable result, so the ordinary run is untouched. But a re-plan issued AFTER
/// a wave finished slices the window past every spawn that produced it, and a merge scoped strictly to that window
/// folds zero results — a live run pushed three accepted agent branches, re-planned three times, then merged 0 and
/// published 0 targets. A plan-generation boundary may supersede an INSTRUCTION; it must not make FINISHED work
/// invisible. So a generation with no mergeable result of its own falls back to the run's earlier Succeeded,
/// not-withheld, not-yet-consolidated agent runs — read off the same append-only tape, never a second source of truth.</para>
///
/// <para>ONE trigger, shared: <see cref="ActiveGenerationHasNoMergeableResult"/>. Both carry-over rungs — this one
/// and DC-3's ledger-direct publish rung (<see cref="SupervisorPublishedBranchResolver"/>) — fire on exactly that
/// predicate and on the same settled-work floor (<see cref="SettledAcrossGenerations"/>), so a tape can never be
/// mergeable-but-unpublishable (or the reverse) purely because the two rungs asked different questions.</para>
/// </summary>
public static class SupervisorMergeContributors
{
    /// <summary>The contributors this turn's <c>merge</c> would fold, and how many of them a plan-generation boundary had stranded.</summary>
    public static SupervisorMergeContributorSelection Resolve(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        ArgumentNullException.ThrowIfNull(priorDecisions);

        if (!ActiveGenerationHasNoMergeableResult(priorDecisions)) return new SupervisorMergeContributorSelection(ActiveGeneration(priorDecisions), 0, 0);

        var carriedOver = StrandedByAReplan(priorDecisions);

        return new SupervisorMergeContributorSelection(carriedOver, carriedOver.Count, AbandonedEarlierResults(priorDecisions));
    }

    /// <summary>
    /// THE carry-over trigger, for both rungs: the active <see cref="SupervisorPlanWindow"/> generation has nothing a
    /// door to the reviewable head may take — it either staged NOTHING at all (the re-plan-after-a-wave case) or
    /// everything it staged is WITHHELD (rejected by its own acceptance grade, or waived). Stated once here because
    /// the two rungs used to differ: the merge fired on "no unwithheld spawn/retry id", the publish rung on "staged
    /// nothing whatsoever", so a generation whose only unit was rejected was mergeable-by-carry-over yet published
    /// nothing at all. A still-RUNNING wave counts as the generation's own work — the ids are staged, and the
    /// withhold door only closes on a settled verdict.
    /// </summary>
    public static bool ActiveGenerationHasNoMergeableResult(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        ArgumentNullException.ThrowIfNull(priorDecisions);

        return StagedNotWithheld(priorDecisions, SupervisorDecisionKinds.StagesAgents).Count == 0;
    }

    /// <summary>
    /// The agent-run ids recorded by EVERY spawn/retry decision in the active <see cref="SupervisorPlanWindow"/>
    /// generation (in order) MINUS any unit a per-unit acceptance grade objectively REJECTED or a human WAIVED
    /// (loopability slice 4, "局部綠≠整合綠"): a unit that failed its OWN definition-of-done must not be integrated
    /// into the reviewable head, even if the model merges. A unit with NO verdict integrates exactly as before.
    /// Narrower than the trigger above by exactly one verb — a <c>resolve</c> stages its own reconciliation branch,
    /// which the head readers surface directly and a merge has never folded as a contributor.
    /// </summary>
    private static IReadOnlyList<Guid> ActiveGeneration(IReadOnlyList<SupervisorPriorDecision> priorDecisions) =>
        StagedNotWithheld(priorDecisions, kind => kind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry);

    /// <summary>The active generation's staged ids for the given staging verbs, in the order they were staged, past the shared <see cref="SupervisorOutcome.IsWithheldFromHead"/> door.</summary>
    private static IReadOnlyList<Guid> StagedNotWithheld(IReadOnlyList<SupervisorPriorDecision> priorDecisions, Func<string, bool> stages)
    {
        var window = SupervisorPlanWindow.Read(priorDecisions).Decisions;
        var withheld = SupervisorOutcome.WithheldAgentRunIds(window);

        return window
            .Where(d => stages(d.DecisionKind))
            .SelectMany(d => SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson))
            .Where(id => !withheld.Contains(id))
            .ToList();
    }

    /// <summary>
    /// Every agent run this supervisor run FINISHED and no merge has CONSOLIDATED, when the active generation has no
    /// mergeable result of its own — <see cref="SettledAcrossGenerations"/> minus what an earlier merge genuinely
    /// folded (this rung's own runaway backstop: re-folding consolidated ids must not read as new progress).
    ///
    /// <para>The exclusion is keyed on <see cref="SupervisorOutcome.MergeConsolidatedContributors"/> and not on the
    /// presence of a <c>merged[]</c> array: that array is written BEFORE the integration is attempted, so a merge
    /// whose integration CONFLICTED records its contributors while landing none of them. Excluding those would
    /// strand the contributors of every conflicted merge permanently — the exact failure this whole class exists to
    /// end. A merge with no integration block at all (the gate is off for that run) still consolidates, exactly as
    /// before. The no-progress backstop is unharmed either way: <c>FoldNoProgressDecisions</c>' own <c>everMerged</c>
    /// accumulator already dedupes across merges, so a second merge over the same stranded tape folds zero NEW
    /// ids.</para>
    /// </summary>
    private static IReadOnlyList<Guid> StrandedByAReplan(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var alreadyConsolidated = priorDecisions
            .Where(SupervisorOutcome.MergeConsolidatedContributors)
            .SelectMany(d => SupervisorOutcome.ReadMergedAgentRunIds(d.OutcomeJson))
            .ToHashSet();

        return SettledAcrossGenerations(priorDecisions).Where(id => !alreadyConsolidated.Contains(id)).ToList();
    }

    /// <summary>
    /// The whole tape's SETTLED work, in the order it was produced — the floor BOTH carry-over rungs read, so the
    /// merge and the publish resolver cannot drift on what a plan-generation boundary is allowed to hide.
    /// Deliberately narrower than <see cref="ActiveGeneration"/>: that path stages ids regardless of status (a
    /// still-running wave is the generation's own work), while a carry-over only ever conserves finished work —
    /// Succeeded, past the same <see cref="SupervisorOutcome.IsWithheldFromHead"/> door (a rejected or waived unit
    /// is no more publishable here than it was mergeable there). Every agent-STAGING verb counts, resolve included:
    /// a resolver's own succeeded branch is the reconciliation of the contributors it replaced, so conserving those
    /// contributors while dropping the resolver would carry over precisely the stale halves.
    ///
    /// <para>The floor's ONE exclusion beyond that: everything staged before <see cref="SinceLatestAbandonment"/>'s
    /// line. Conservation answers "the model re-planned AFTER the work landed"; it must not also answer "the model
    /// re-planned BECAUSE the work was the wrong direction", and only the model can tell those apart — so the discard
    /// is its explicit declaration, never an inference from the boundary. Read off that ONE function, which every
    /// other reader of abandoned work reads too, so no rung can be left crediting what another rung dropped.</para>
    ///
    /// <para>Says nothing about what a prior <c>merge</c> already folded — that exclusion belongs to the merge rung
    /// alone (<see cref="StrandedByAReplan"/>). DC-3's ledger-direct publish rung must NOT inherit it: that rung only
    /// runs when NO merge integrated a branch at all (it is gated on
    /// <see cref="SupervisorOutcome.AnyMergeIntegratedABranch"/>), and there a contributor's own pushed branch is
    /// still the only genuinely published artifact — which is also how that resolver treats merged contributors
    /// today, since it reads the manifest ledger and never consults a merge outcome.</para>
    /// </summary>
    public static IReadOnlyList<Guid> SettledAcrossGenerations(IReadOnlyList<SupervisorPriorDecision> priorDecisions) =>
        Settled(SinceLatestAbandonment(priorDecisions));

    /// <summary>
    /// THE abandonment boundary, as a tape: everything from the newest plan that declared
    /// <see cref="SupervisorPlanPayload.AbandonEarlierResults"/> onward, or the WHOLE tape verbatim (same instance,
    /// allocation-free) when no plan abandoned anything — which is every run that never emits the signal.
    ///
    /// <para>ONE function, because the discard has to hold on every reader that can put earlier work in front of a
    /// human, not just on the settled-work floor below. The floor alone left the ladder's upper rungs
    /// (<see cref="SupervisorPublishedBranchResolver.ResolveAsync"/>'s integrated-head reads) and the completion
    /// authority's Integrate cell (<c>UpstreamStageTrace</c>) reading the whole tape, so an abandoned generation's
    /// cleanly-merged head was still published and still credited as integration work — the flag revoked the merge
    /// and nothing else. Those readers have no barrier that a <c>plan</c> trips
    /// (<see cref="SupervisorOutcome.ReadFinalIntegratedBranchWithin"/> stops only at agent-STAGING work), so they
    /// cannot notice the line on their own; they have to be handed a tape that already ends at it.</para>
    /// </summary>
    public static IReadOnlyList<SupervisorPriorDecision> SinceLatestAbandonment(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        ArgumentNullException.ThrowIfNull(priorDecisions);

        var boundary = AbandonBoundary(priorDecisions);

        return boundary <= 0 ? priorDecisions : priorDecisions.Skip(boundary).ToArray();
    }

    /// <summary>
    /// How many settled results the newest ABANDONING plan removed from the floor above — the plan's own receipt
    /// (<c>RealSupervisorActionExecutor.ExecutePlanAsync</c> records it) and the number the recitation tells the brain.
    /// 0 when no plan abandoned anything, which is every run that never emits the signal.
    /// </summary>
    public static int AbandonedEarlierResults(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        ArgumentNullException.ThrowIfNull(priorDecisions);

        var boundary = AbandonBoundary(priorDecisions);

        return boundary < 0 ? 0 : Settled(priorDecisions.Take(boundary).ToArray()).Count;
    }

    /// <summary>
    /// The tape index of the NEWEST plan that declared <see cref="SupervisorPlanPayload.AbandonEarlierResults"/>, or
    /// -1 when none did. Newest-first, so abandonment is MONOTONIC: a later plan that says nothing leaves the line
    /// where the abandoning plan drew it rather than un-abandoning work the model already called the wrong direction.
    /// Only a plan that actually OPENS a generation (<see cref="SupervisorPlanWindow.IsValidBoundary"/>) may draw one —
    /// a malformed or subtask-less plan is not a boundary anywhere else, and it must not become one here, where the
    /// consequence is destroying finished work.
    /// </summary>
    private static int AbandonBoundary(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        for (var i = priorDecisions.Count - 1; i >= 0; i--)
            if (SupervisorPlanWindow.IsValidBoundary(priorDecisions[i]) && SupervisorOutcome.ReadPlanAbandonsEarlierResults(priorDecisions[i].PayloadJson))
                return i;

        return -1;
    }

    /// <summary>The settled, unwithheld agent-run ids the given slice of the tape staged, in the order they were produced.</summary>
    private static IReadOnlyList<Guid> Settled(IReadOnlyList<SupervisorPriorDecision> decisions) =>
        decisions
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .Where(r => r.Status == nameof(AgentRunStatus.Succeeded) && !SupervisorOutcome.IsWithheldFromHead(r))
            .Select(r => r.AgentRunId)
            .Distinct()
            .ToList();
}

/// <summary>The contributors one <c>merge</c> folds, how many of them a plan-generation boundary would otherwise have stranded, and how many a plan explicitly ABANDONED. A pure read of the tape.</summary>
public sealed record SupervisorMergeContributorSelection(IReadOnlyList<Guid> AgentRunIds, int CarriedOverFromEarlierGenerations, int AbandonedFromEarlierGenerations);
