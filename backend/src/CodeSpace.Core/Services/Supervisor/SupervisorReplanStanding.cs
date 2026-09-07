using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// What a PLAN authored over a unit's standing acceptance verdict has actually accomplished — the fixed point the
/// decider's two re-plan steers used to feed. Pure over the tape (a replay re-derives the identical answer), off
/// the same two authorities the rest of the supervisor reads: <see cref="SupervisorPlanWindow.IsValidBoundary"/>
/// for what counts as a re-plan, and the positional <c>subtaskIds[i] ↔ agentResults[i]</c> join
/// <see cref="SupervisorDependencyGate.SubtaskIdsOf"/> publishes for which attempts belong to a unit.
///
/// <para>THE TWO READINGS, stated once, because collapsing them is the defect this file was rewritten to fix. A
/// unit whose standing verdict is a graded failure and over which a valid plan generation has since opened is in
/// exactly one of two states:
/// <list type="bullet">
///   <item><see cref="AwaitsItsReplannedStaging"/> — nothing has re-graded it since. The re-plan is UNRUN, not
///         spent: the plan may well have fixed the check, and the only way to find out is to stage it. The first
///         cut of this reading counted this as "the verdict survived a re-plan" (the standing row matched ITSELF in
///         the detail comparison, so the rule collapsed to "∃ a valid plan boundary after this verdict"), which
///         fired on the FIRST turn after every re-plan — precisely when the model had just obeyed "re-plan this
///         item with a check its agent can satisfy" — and then named neither <c>spawn</c> nor <c>retry</c>, the
///         only verbs that could grade the repaired check. That is a second attractor, not a fix for the
///         first.</item>
///   <item><see cref="VerdictSurvivedAReplan"/> — an attempt UNDER the new generation came back with the identical
///         <c>AcceptanceDetail</c>. THIS is the evidence that another plan is a move the run has already made
///         against this unit and it left the verdict where it found it.</item>
/// </list></para>
///
/// <para>Anchored on <see cref="SupervisorPlanWindow.IsValidBoundary"/> rather than on the decision KIND alone
/// (which is what <see cref="SupervisorAmendObligation"/> anchors its co-sign invalidation on, for its own MAJOR-8
/// reason): a plan that opened no generation — failed, empty, structurally invalid — re-planned nothing, so it must
/// not be able to withdraw the plan verb from a unit that has never actually had a second plan authored over it.
/// The two anchors cannot render contradictory prompt text, because the amend standings this reading yields to
/// (<see cref="SupervisorAmendStanding.AwaitingRetry"/> / <see cref="SupervisorAmendStanding.Consumed"/> /
/// <see cref="SupervisorAmendStanding.Discarded"/>) own their own steers and are read FIRST at every render
/// site — the decider's <c>InfraSteerFor</c> switch, the recitation's state line, and the evidence tail's
/// preamble.</para>
/// </summary>
public static class SupervisorReplanStanding
{
    /// <summary>Where this unit must be sent instead of at another plan, or <see cref="SupervisorReplanExit.None"/> when a re-plan is still an honest move. The admissibility half is the server's own amend gate, never a second reading of it, so the steer can only ever name a verb the turn's roster also offers. The converse does NOT hold, on purpose: a unit the newest plan dropped is steered at the human even though the gate would still admit an amendment for it — withholding a steer is free, and a co-sign no retry can consume is not.</summary>
    public static SupervisorReplanExit ExitFor(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId) =>
        subtaskId is null ? SupervisorReplanExit.None : ExitFor(priorDecisions, subtaskId, AttemptsOf(priorDecisions, subtaskId), NewestGeneration(priorDecisions));

    /// <summary>
    /// EVERY staged unit's exit off ONE walk of the tape — the entry point a prompt build uses, because
    /// <see cref="ExitFor(IReadOnlyList{SupervisorPriorDecision}, string?)"/> re-walks it (re-parsing every staging
    /// decision's <c>OutcomeJson</c>) per call and the decider renders the same unit once per attempt it appears
    /// in. Keyed by subtask id, with an entry for every unit the tape ever staged a folded result for — including
    /// the <see cref="SupervisorReplanExit.None"/> ones, so a caller reads "no exit" and "not on this tape"
    /// identically instead of asking again.
    /// </summary>
    public static IReadOnlyDictionary<string, SupervisorReplanExit> ExitsFor(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var generation = NewestGeneration(priorDecisions);
        var exits = new Dictionary<string, SupervisorReplanExit>(StringComparer.Ordinal);

        foreach (var (subtaskId, attempts) in AttemptsBySubtask(priorDecisions))
            exits[subtaskId] = ExitFor(priorDecisions, subtaskId, attempts, generation);

        return exits;
    }

    /// <summary>
    /// The TAPE half of the re-graded arm, with no policy in it: a plan generation opened over this unit's graded
    /// failure, an attempt UNDER that generation re-graded it, and the verdict came back identical — so another
    /// plan is a move this run has already made here for nothing. False for a unit that was never graded, whose
    /// latest attempt passed, whose verification a human waived, that no valid plan has followed, or that the
    /// re-plan has not re-graded yet (that unit is <see cref="AwaitsItsReplannedStaging"/>).
    ///
    /// <para>FAILS OPEN on a volatile detail, deliberately. The comparison is ordinal over the whole
    /// <c>AcceptanceDetail</c> string, and some details carry run-local bytes — a <c>grade-error: …</c> can quote a
    /// workspace path, a timeout can quote elapsed seconds — so two identical failures of a genuinely unchanged
    /// check can read as different verdicts and this returns false. The consequence is the FIRST-TIME copy for one
    /// more turn, never a withdrawn verb on a unit that still has a move: the ramp is a prohibition, and a
    /// prohibition that under-fires costs a turn while one that over-fires strands the run.</para>
    /// </summary>
    public static bool VerdictSurvivedAReplan(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId) =>
        subtaskId is not null && ReGradedIdentically(AttemptsOf(priorDecisions, subtaskId), NewestGeneration(priorDecisions));

    /// <summary>The TAPE half of the other arm: a valid plan generation opened AFTER this unit's standing graded failure and nothing has been staged for the unit since. The plan is authored and unrun — the run's own next move, and the one the model must not replace with a third plan.</summary>
    public static bool AwaitsItsReplannedStaging(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId) =>
        subtaskId is not null && AwaitsStaging(AttemptsOf(priorDecisions, subtaskId), NewestGeneration(priorDecisions));

    /// <summary>The ONE resolution both entry points funnel through, over the attempts and the generation each resolved its own way. Ordered so the free tape facts rule before the amend gate is consulted at all — that gate is the only reader here that walks the tape again.</summary>
    private static SupervisorReplanExit ExitFor(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string subtaskId, IReadOnlyList<Attempt> attempts, SupervisorPriorDecision? generation)
    {
        var reGraded = ReGradedIdentically(attempts, generation);

        if (!reGraded && !AwaitsStaging(attempts, generation)) return SupervisorReplanExit.None;

        // A unit the NEWEST plan no longer declares has neither of the two live exits available to it, whatever the
        // tape says about its verdict: a spawn cannot stage a unit the plan does not declare, and an amendment
        // co-signed for it would mint a human ruling no retry could ever consume (the residual
        // SupervisorAmendPrecondition.GradedAttempts names and declines to guard, because the gate reads the whole
        // tape on purpose). A human is the only door.
        if (!Declares(generation!, subtaskId)) return SupervisorReplanExit.ToHuman;

        if (!reGraded) return SupervisorReplanExit.ToStaging;

        return SupervisorAmendPrecondition.IsAmendable(priorDecisions, subtaskId) ? SupervisorReplanExit.ToAmendment : SupervisorReplanExit.ToHuman;
    }

    /// <summary>An attempt under the newest generation re-graded this unit and returned the verdict it already had before the generation opened.</summary>
    private static bool ReGradedIdentically(IReadOnlyList<Attempt> attempts, SupervisorPriorDecision? generation)
    {
        if (generation is null || StandingFailure(attempts) is not { } standing || standing.Sequence < generation.Sequence) return false;

        return attempts.Any(a => a.Sequence < generation.Sequence && IsGradedFailure(a.Result)
                                 && string.Equals(a.Result.AcceptanceDetail, standing.Result.AcceptanceDetail, StringComparison.Ordinal));
    }

    /// <summary>The newest generation opened AFTER this unit's standing graded failure — so the plan it authored for the unit has never been run.</summary>
    private static bool AwaitsStaging(IReadOnlyList<Attempt> attempts, SupervisorPriorDecision? generation) =>
        generation is not null && StandingFailure(attempts) is { } standing && standing.Sequence < generation.Sequence;

    /// <summary>Whether the plan that opened this generation still declares the unit — read off the boundary decision's own payload, so it is the same plan <see cref="SupervisorPlanWindow"/> draws the window from.</summary>
    private static bool Declares(SupervisorPriorDecision generation, string subtaskId) =>
        SupervisorOutcome.ReadPlanSubtasks(generation.PayloadJson).Any(s => string.Equals(s.Id, subtaskId, StringComparison.Ordinal));

    /// <summary>This unit's STANDING verdict — its latest attempt's — when that verdict is a graded failure, else null (never staged, latest attempt passed, ungraded pass-through, or a human waived the verification).</summary>
    private static Attempt? StandingFailure(IReadOnlyList<Attempt> attempts) =>
        attempts.Count > 0 && IsGradedFailure(attempts[^1].Result) ? attempts[^1] : null;

    /// <summary>A verdict the server actually graded and REFUSED — never an ungraded pass-through, and never a human waiver (WAIVED ≠ FAILED at every door, the B2 invariant).</summary>
    private static bool IsGradedFailure(SupervisorAgentResult result) => result.AcceptancePassed == false && !SupervisorOutcome.IsWaived(result);

    /// <summary>The newest decision that OPENS a plan generation, or null when nothing on the tape does.</summary>
    private static SupervisorPriorDecision? NewestGeneration(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        for (var i = priorDecisions.Count - 1; i >= 0; i--)
            if (SupervisorPlanWindow.IsValidBoundary(priorDecisions[i])) return priorDecisions[i];

        return null;
    }

    /// <summary>This unit's slice of <see cref="AttemptsBySubtask"/> — the single-unit entry points' walk.</summary>
    private static IReadOnlyList<Attempt> AttemptsOf(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string subtaskId) =>
        AttemptsBySubtask(priorDecisions).TryGetValue(subtaskId, out var attempts) ? attempts : Array.Empty<Attempt>();

    /// <summary>EVERY folded attempt of EVERY staged subtask, in tape order, with the sequence of the staging decision that produced it — the all-attempts sibling of <c>SupervisorDependencyGate.LatestResultsBySubtask</c>, which keeps only the last. The scope is the whole tape on purpose: the attempt a re-plan closed the plan window over is exactly the evidence this reading compares against.</summary>
    private static Dictionary<string, List<Attempt>> AttemptsBySubtask(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var attempts = new Dictionary<string, List<Attempt>>(StringComparer.Ordinal);

        foreach (var prior in priorDecisions.Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind)))
        {
            var ids = SupervisorDependencyGate.SubtaskIdsOf(prior);
            var results = SupervisorOutcome.ReadAgentResults(prior.OutcomeJson);

            for (var i = 0; i < ids.Count && i < results.Count; i++)
            {
                if (!attempts.TryGetValue(ids[i], out var unit)) attempts[ids[i]] = unit = new List<Attempt>();

                unit.Add(new Attempt(prior.Sequence, results[i]));
            }
        }

        return attempts;
    }

    /// <summary>One folded attempt of a unit, with the sequence of the staging decision that produced it.</summary>
    private readonly record struct Attempt(long Sequence, SupervisorAgentResult Result);
}
