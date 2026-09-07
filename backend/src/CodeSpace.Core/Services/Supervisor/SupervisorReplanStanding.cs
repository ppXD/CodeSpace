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
///   <item><see cref="VerdictSurvivedAReplan"/> — an attempt UNDER a new generation came back with the identical
///         <c>AcceptanceDetail</c>. THIS is the evidence that another plan is a move the run has already made
///         against this unit and it left the verdict where it found it.</item>
/// </list></para>
///
/// <para>Those two are not exclusive, and the ORDER is load-bearing: the re-graded reading is over ANY boundary the
/// unit was re-graded across and it WINS (<see cref="ExitFor(IReadOnlyList{SupervisorPriorDecision}, string?)"/>).
/// Read against the NEWEST generation only, it had a hole the size of the loop it closes — a tape whose re-grade
/// came back identical is sent at the amendment, the model authors one more PLAN instead, that plan moves the
/// newest boundary PAST the evidence, the reading falls back to "authored and unrun", and the run cycles
/// <c>spawn → identical re-grade → amend → plan → …</c> forever. Nothing bounds that cycle except the total-spawn
/// and cost caps: an infra-classed rejection with work present deliberately KEEPS its settled evidence
/// (<see cref="SupervisorOutcome.HasSettledEvidence"/>), so every staging turn of the cycle counts as progress and
/// the no-progress streak resets. The tape's memory that a re-plan already failed to move THIS verdict must not
/// be erasable by authoring another one.</para>
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
    public static SupervisorReplanExit ExitFor(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId)
    {
        if (subtaskId is null) return SupervisorReplanExit.None;

        var boundaries = ValidBoundaries(priorDecisions);

        return ExitFor(priorDecisions, subtaskId, AttemptsOf(priorDecisions, subtaskId), new TapeWideReads(NewestGeneration(boundaries), BlockedSubtaskIds(priorDecisions), boundaries));
    }

    /// <summary>
    /// EVERY staged unit's exit off ONE walk of the ATTEMPT index — the entry point a prompt build uses, because
    /// <see cref="ExitFor(IReadOnlyList{SupervisorPriorDecision}, string?)"/> re-walks it (re-parsing every staging
    /// decision's <c>OutcomeJson</c>) per call and the decider renders the same unit once per attempt it appears
    /// in. The tape-wide reads it shares across units — the newest generation, the valid plan boundaries, the
    /// dependency frontier — are resolved once here too, so the re-graded arm's own boundary lookup
    /// (<see cref="NewestGenerationBefore"/>) becomes a scan of that short precomputed list instead of re-parsing
    /// and re-validating every Plan decision's payload for every unit that reaches it. NOT every read: once a unit
    /// clears that lookup, the amend gate it may still be sent through walks the tape again per unit it is asked
    /// about, and that is deliberate (a second reading of admissibility is the one thing this file must never
    /// carry). Keyed by subtask id, with an entry for every unit the tape ever staged a folded result for —
    /// including the <see cref="SupervisorReplanExit.None"/> ones, so a caller reads "no exit" and "not on this
    /// tape" identically instead of asking again.
    /// </summary>
    public static IReadOnlyDictionary<string, SupervisorReplanExit> ExitsFor(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var boundaries = ValidBoundaries(priorDecisions);
        var reads = new TapeWideReads(NewestGeneration(boundaries), BlockedSubtaskIds(priorDecisions), boundaries);
        var exits = new Dictionary<string, SupervisorReplanExit>(StringComparer.Ordinal);

        foreach (var (subtaskId, attempts) in AttemptsBySubtask(priorDecisions))
            exits[subtaskId] = ExitFor(priorDecisions, subtaskId, attempts, reads);

        return exits;
    }

    /// <summary>
    /// The TAPE half of the re-graded arm, with no policy in it: a plan generation opened over a graded failure of
    /// this unit, an attempt UNDER that generation re-graded it, and the verdict came back identical to the one the
    /// unit STILL stands on — so another plan is a move this run has already made here for nothing. ANY valid
    /// boundary the unit was re-graded across counts, not only the newest: see the class remarks for the cycle that
    /// scoping it to the newest generation left open. False for a unit that was never graded, whose latest attempt
    /// passed, whose verification a human waived, whose verdict a re-plan actually MOVED, that no valid plan has
    /// followed, or that the re-plan has not re-graded yet (that unit is <see cref="AwaitsItsReplannedStaging"/>).
    ///
    /// <para>FAILS OPEN on a volatile detail, deliberately. The comparison is ordinal over the whole
    /// <c>AcceptanceDetail</c> string, and some details carry run-local bytes — a <c>grade-error: …</c> can quote a
    /// workspace path, a timeout can quote elapsed seconds — so two identical failures of a genuinely unchanged
    /// check can read as different verdicts and this returns false. The consequence is the FIRST-TIME copy for one
    /// more turn, never a withdrawn verb on a unit that still has a move: the ramp is a prohibition, and a
    /// prohibition that under-fires costs a turn while one that over-fires strands the run.</para>
    ///
    /// <para>It fails open the same way on an UNGRADED attempt under the new plan — a shell that died before the
    /// check could run folds a result with no verdict at all, which is neither a standing graded failure to compare
    /// nor an unrun plan, so BOTH arms read false and the first-time re-plan copy renders again for that one cycle.
    /// The next graded attempt re-anchors the reading. Left as-is rather than treated as a re-grade: an attempt that
    /// never reached the check is no evidence about what the re-planned check does.</para>
    /// </summary>
    public static bool VerdictSurvivedAReplan(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId) =>
        subtaskId is not null && ReGradedIdentically(AttemptsOf(priorDecisions, subtaskId), ValidBoundaries(priorDecisions));

    /// <summary>The TAPE half of the other arm: a valid plan generation opened AFTER this unit's standing graded failure and nothing has been staged for the unit since. The plan is authored and unrun — the run's own next move, and the one the model must not replace with a third plan. TRUE does not by itself decide the exit: a unit that ALSO carries an identical re-grade across an earlier boundary is sent at the amendment instead (<see cref="ExitFor(IReadOnlyList{SupervisorPriorDecision}, string?)"/> orders them), because a plan authored over spent evidence must not launder it.</summary>
    public static bool AwaitsItsReplannedStaging(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? subtaskId) =>
        subtaskId is not null && AwaitsStaging(AttemptsOf(priorDecisions, subtaskId), NewestGeneration(ValidBoundaries(priorDecisions)));

    /// <summary>The three tape-wide facts <see cref="ExitsFor"/> resolves ONCE and threads through every unit's resolution — grouped so the per-unit call stays within a sane parameter count and no unit pays twice for a fact the whole tape only needed answering once.</summary>
    private readonly record struct TapeWideReads(SupervisorPriorDecision? Generation, IReadOnlySet<string> Blocked, IReadOnlyList<SupervisorPriorDecision> Boundaries);

    /// <summary>The ONE resolution both entry points funnel through, over the attempts and the tape-wide reads each resolved its own way. Ordered so the free tape facts rule before the amend gate is consulted at all — that gate is the only reader here that walks the tape again.</summary>
    private static SupervisorReplanExit ExitFor(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string subtaskId, IReadOnlyList<Attempt> attempts, TapeWideReads reads)
    {
        var reGraded = ReGradedIdentically(attempts, reads.Boundaries);

        if (!reGraded && !AwaitsStaging(attempts, reads.Generation)) return SupervisorReplanExit.None;

        // A unit the NEWEST plan no longer declares has neither of the two live exits available to it, whatever the
        // tape says about its verdict: a spawn cannot stage a unit the plan does not declare, and an amendment
        // co-signed for it would mint a human ruling no retry could ever consume (the residual
        // SupervisorAmendPrecondition.GradedAttempts names and declines to guard, because the gate reads the whole
        // tape on purpose). A human is the only door.
        if (!Declares(reads.Generation!, subtaskId)) return SupervisorReplanExit.ToHuman;

        // The staging arm, ORDERED by the rail that clamps it: the dependency gate reads "satisfied" inside the
        // CURRENT generation only, so a re-declared unit whose dependency was accepted under an EARLIER plan is
        // deferred again — and the spawn this arm would name unqualified stages nothing (an all-deferred spawn is
        // accepted-empty) one screen from a frontier block calling the unit blocked. Still the staging, never
        // another plan; only its ordering is stated.
        if (!reGraded) return reads.Blocked.Contains(subtaskId) ? SupervisorReplanExit.ToStagingBehindADependency : SupervisorReplanExit.ToStaging;

        return SupervisorAmendPrecondition.IsAmendable(priorDecisions, subtaskId) ? SupervisorReplanExit.ToAmendment : SupervisorReplanExit.ToHuman;
    }

    /// <summary>An attempt re-graded this unit ACROSS a valid plan boundary and returned the verdict it already had before that boundary opened — the boundary being the newest one the unit's STANDING verdict was graded after, never necessarily the newest on the tape. <paramref name="boundaries"/> is every valid boundary on the tape, oldest first (<see cref="ValidBoundaries"/>), so this never re-parses or re-validates a Plan payload itself.</summary>
    private static bool ReGradedIdentically(IReadOnlyList<Attempt> attempts, IReadOnlyList<SupervisorPriorDecision> boundaries)
    {
        if (StandingFailure(attempts) is not { } standing) return false;
        if (NewestGenerationBefore(boundaries, standing.Sequence) is not { } boundary) return false;

        return attempts.Any(a => a.Sequence < boundary.Sequence && IsGradedFailure(a.Result)
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

    /// <summary>Every decision on the tape that OPENS a plan generation, tape order preserved — computed ONCE per call so <see cref="NewestGenerationBefore"/> is a scan over a short list of already-valid boundaries rather than re-parsing and re-validating (<see cref="SupervisorPlanWindow.IsValidBoundary"/>) every Plan decision's payload for every unit that asks.</summary>
    private static IReadOnlyList<SupervisorPriorDecision> ValidBoundaries(IReadOnlyList<SupervisorPriorDecision> priorDecisions) =>
        priorDecisions.Where(SupervisorPlanWindow.IsValidBoundary).ToList();

    /// <summary>The newest decision that OPENS a plan generation, or null when <paramref name="boundaries"/> is empty.</summary>
    private static SupervisorPriorDecision? NewestGeneration(IReadOnlyList<SupervisorPriorDecision> boundaries) =>
        NewestGenerationBefore(boundaries, long.MaxValue);

    /// <summary>The newest generation opened BEFORE a given sequence, or null when none was. The newest such boundary is the only one worth asking about: every earlier boundary admits a SUBSET of the prior attempts as the "before" side of a re-grade, so if any boundary witnesses one, this one does.</summary>
    private static SupervisorPriorDecision? NewestGenerationBefore(IReadOnlyList<SupervisorPriorDecision> boundaries, long sequence)
    {
        for (var i = boundaries.Count - 1; i >= 0; i--)
            if (boundaries[i].Sequence < sequence) return boundaries[i];

        return null;
    }

    /// <summary>The planned units the dependency rail is still DEFERRING — read off the gate's own frontier so the ordered staging arm and the prompt's frontier block can never disagree about which units are blocked. Empty for a flat plan (no <c>DependsOn</c> edges at all), which is the byte-identical common case.</summary>
    private static IReadOnlySet<string> BlockedSubtaskIds(IReadOnlyList<SupervisorPriorDecision> priorDecisions) =>
        SupervisorDependencyGate.Frontier(priorDecisions).Blocked.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);

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

    /// <summary>
    /// Whether a verdict of this SHAPE renders an exit at all — the ONE predicate the decider's verdict line and
    /// the recitation's authoring lint share. It is TRUE on exactly the two arms that substitute the exit ramp for
    /// their own re-plan sentence: an <see cref="InfraClassed"/> failure, and a work-classed failure whose BASE
    /// tree measurably fails the same check (<see cref="BaselineAlsoFails"/>). A work rejection against a GREEN or
    /// unmeasured baseline is steered at the RETRY instead and carries no ramp — so the lint's "take the exit its
    /// verdict names above" would defer to nothing there, which is the whole reason this predicate is not simply
    /// "the exit is not None".
    /// </summary>
    public static bool VerdictNamesTheExit(SupervisorAgentResult result) =>
        result.AcceptancePassed == false && !SupervisorOutcome.IsWaived(result) && (InfraClassed(result) || BaselineAlsoFails(result));

    /// <summary>The unit's CHECK could not run (grader fault, environment, half-authored spec) — the shared classification, over the same work-presence read every other door applies. The decider's verdict line reads its first arm from here so the ramp's render condition has one definition.</summary>
    public static bool InfraClassed(SupervisorAgentResult result) =>
        Agents.AgentAcceptanceContract.IsInfraFailure(result.AcceptanceDetail, SupervisorOutcome.ResultShowsWork(result));

    /// <summary>A work-classed failure whose BASE tree MEASURABLY fails the same check — pre-existing breakage a blind retry cannot fix, and the decider's second re-plan steer. An UNMEASURED baseline (never captured, or itself infra-classed) claims nothing.</summary>
    public static bool BaselineAlsoFails(SupervisorAgentResult result) =>
        !InfraClassed(result) && result.BaselinePassed == false && !Agents.AgentAcceptanceContract.IsInfraFailure(result.BaselineDetail, workPresent: true);

    /// <summary>One folded attempt of a unit, with the sequence of the staging decision that produced it.</summary>
    private readonly record struct Attempt(long Sequence, SupervisorAgentResult Result);
}
