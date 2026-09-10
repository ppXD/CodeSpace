using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Quality;

namespace CodeSpace.Core.Services.Quality;

/// <summary>
/// The PURE, DATA-DRIVEN policy that chooses WHICH quality mechanism earns the next increment of budget (P22) —
/// pure static, no DI, no I/O, mirroring <c>EffortPolicy</c>'s shape: a small ORDERED table (<see cref="Rows"/>) of
/// <c>predicate(facts) → mechanism + reason</c>, evaluated first-match, with a final <c>_ => true</c> catch-all that
/// makes the table TOTAL (every fact combination resolves; <see cref="Decide"/> can never return null or throw).
///
/// <para><b>Chosen by evidence, never by a switch.</b> Every predicate reads only <see cref="QualityDecisionInput"/>,
/// whose shape CANNOT carry a task name, a provider, a model, a repository or a path (its purity invariant is
/// reflection-enforced against an allow-list of carried types). So "which mechanism does this kind of task get?" is
/// not a question this policy can be asked — it only answers "what does the recorded evidence say the next
/// increment should buy?". A new task phrasing or repository shape needs no new case, because there is no case to
/// add.</para>
///
/// <para><b>Ordering is soundness, not taste.</b> The two zero-cost human exits come FIRST — a recorded human
/// verdict and a human's waiver both outrank a budget or no-progress computation, because a human's decision is
/// not something arithmetic may override; the affordability and no-progress gates follow because a mechanism
/// nobody can pay for, or a run out of decisions, is not an option; the machinery-failed row precedes every row
/// that spends on the work, so a check that could not run never buys a stronger model; the disputed row precedes
/// the goal-met row so a passing check other recorded evidence disagrees with still buys an independent look
/// before shipping; and the rows that BUY EVIDENCE precede the rows that spend on production, because a mechanism
/// chosen without evidence is the thing P22 exists to stop. Every pair that can actually collide on one input is
/// pinned by <c>QualityPolicyTests.Orderings</c>, so a reorder is a test-visible decision rather than a silent
/// behaviour change.</para>
///
/// <para><b>Every row has an exit.</b> A row that fires on a condition its own mechanism cannot change is a loop,
/// not a policy: the no-check row is guarded on <see cref="QualityDecisionInput.IndependentReviewRecorded"/> so the
/// review it buys satisfies it, and the review's own verdict then either stops the work or disputes it. Anything
/// that buys evidence must be answerable by the evidence it buys.</para>
///
/// <para><b>Every mechanism is provisional.</b> P22-9c's same-budget ablation harness measures each mechanism
/// against spending the same budget on more <see cref="QualityMechanism.SingleAgent"/> attempts; a row whose
/// mechanism never wins its ablation is DELETED rather than kept as an unmeasured option, and the four thresholds
/// below are the boundaries that ablation moves. P22-9b wires this policy into the supervisor / executor decision
/// points behind the P14 decider lease — until then nothing calls it and no production behaviour depends on it.</para>
/// </summary>
public static class QualityPolicy
{
    /// <summary>Recorded review scores at-or-above this (0–100) are not treated as uncertainty. A guess until P22-9c's ablation measures it; 9c's ablation may move this. Pinned by test so moving it is a deliberate edit.</summary>
    public const int ConfidentReviewScoreFloor = 70;

    /// <summary>How many consecutive failed verdicts make a REPEAT. Two: the second failure is the first repetition, and the first is simply a failure. 9c's ablation may move this. Pinned by test.</summary>
    public const int RepeatedFailureFloor = 2;

    /// <summary>Changed-file count at-or-above which repeated failure prefers splitting over escalating — a conventional small-diff ceiling. A guess; 9c's ablation may move this. Pinned by test.</summary>
    public const int SplitScaleFileFloor = 10;

    /// <summary>Workspace-unit count at-or-above which the work already has seams to split along. Two: a second independently-checkoutable unit IS a seam the plan did not have to invent. 9c's ablation may move this. Pinned by test.</summary>
    public const int SplitScaleUnitFloor = 2;

    /// <summary>
    /// The mechanism the recorded evidence chooses, with the evidence that chose it. First matching row wins; the
    /// catch-all guarantees a result.
    /// </summary>
    public static QualityDecision Decide(QualityDecisionInput facts)
    {
        var row = Rows.First(r => r.Matches(facts));

        return new QualityDecision { Mechanism = row.Mechanism, Reason = row.Reason(facts) };
    }

    /// <summary>
    /// The ORDERED rule table — first match wins, read top-to-bottom as "what does the evidence already rule out?".
    /// The zero-cost exits and the evidence-buying mechanisms come before the production-spending ones; the last
    /// row's <c>_ => true</c> predicate makes the table total.
    /// </summary>
    private static readonly IReadOnlyList<PolicyRow> Rows = new[]
    {
        new PolicyRow(AHumanVerdictIsRequired, QualityMechanism.AskHuman, HumanRequiredReason),
        new PolicyRow(AHumanWaivedVerification, QualityMechanism.Stop, WaivedReason),
        new PolicyRow(CannotAffordAnotherAttempt, QualityMechanism.Stop, ExhaustedBudgetReason),
        new PolicyRow(NoProgressCapIsReached, QualityMechanism.Stop, NoProgressReason),
        new PolicyRow(TheCheckMachineryFailed, QualityMechanism.BoundedRepair, MachineryFailedReason),
        new PolicyRow(ADeclaredCheckNeverRan, QualityMechanism.BoundedRepair, UnrunCheckReason),
        new PolicyRow(NothingCanGradeTheWorkYet, QualityMechanism.IndependentCritic, NoCheckReason),
        new PolicyRow(ThePassedCheckIsDisputed, QualityMechanism.IndependentCritic, DisputedEvidenceReason),
        new PolicyRow(TheCheckPassed, QualityMechanism.Stop, GoalMetReason),
        new PolicyRow(FailureRepeatsAtScale, QualityMechanism.SplitIntoSubtasks, RepeatAtScaleReason),
        new PolicyRow(FailureRepeats, QualityMechanism.EscalateModel, RepeatLocalizedReason),
        new PolicyRow(TheOnlyAvailableEvidenceApproves, QualityMechanism.Stop, ReviewApprovedReason),
        new PolicyRow(_ => true, QualityMechanism.SingleAgent, NoBlockingEvidenceReason),
    };

    // ── Predicates: each reads recorded facts only ──────────────────────────────────────────────────────

    /// <summary>The recorded remainder cannot pay for one more attempt at the recorded rate. Uncapped work, or work whose next-attempt cost is unrecorded, can never match — the policy stops for budget only on evidence, never on suspicion.</summary>
    private static bool CannotAffordAnotherAttempt(QualityDecisionInput f) => f.RemainingUsd is { } remaining && f.EstimatedNextAttemptCostUsd is { } next && remaining < next;

    /// <summary>A recorded verdict says a HUMAN must decide. The only route to <see cref="QualityMechanism.AskHuman"/> — it is never a generic fallback for work the table found hard.</summary>
    private static bool AHumanVerdictIsRequired(QualityDecisionInput f) => f.LatestDisposition == VerificationDisposition.HumanReviewRequired;

    /// <summary>The recorded consecutive-no-progress count reached its recorded cap. Unmatched when no cap is recorded.</summary>
    private static bool NoProgressCapIsReached(QualityDecisionInput f) => f.MaxNoProgressDecisions is { } cap && f.NoProgressDecisions >= cap;

    /// <summary>The MACHINERY failed, not the work — the one recorded classification that says so (<c>Classify</c>'s infra arm). Placed before every row that spends on the work, so a stronger model is never bought to fix a check that could not run.</summary>
    /// <para>Bounded by nothing INSIDE this row: repeated repair attempts are capped only by the caller's own bounds above it — the recorded no-progress cap (<c>SupervisorTurnContext.MaxNoProgressDecisions</c>, default 8) and the budget rows. This row deliberately adds no separate repair-count floor of its own.</para>
    private static bool TheCheckMachineryFailed(QualityDecisionInput f) => f.LatestDisposition == VerificationDisposition.InfraUnknown;

    /// <summary>A HUMAN authorized forgoing verification for this work. No automated mechanism may spend past a human's authorization to stop — and equally, a waiver is never read here as a recorded pass (the amend-acceptance FATAL-1 invariant): the mechanism is the same Stop, the evidence is emphatically not.</summary>
    private static bool AHumanWaivedVerification(QualityDecisionInput f) => f.LatestDisposition == VerificationDisposition.Waived;

    /// <summary>A check WAS declared, work HAS been attempted, and there is still no verdict — the evidence the work was supposed to produce never got produced. An approving review cannot stand in for it (the row that stops on a review is guarded on there being no declared check).</summary>
    /// <para>Bounded by nothing INSIDE this row either: the same caller bounds — the recorded no-progress cap and the budget rows above it — are what eventually stop a check that keeps failing to run, not a repair-count floor added here.</para>
    private static bool ADeclaredCheckNeverRan(QualityDecisionInput f) => AnObjectiveCheckCanGrade(f) && WorkHasBeenAttempted(f) && f.LatestDisposition == VerificationDisposition.Unknown;

    /// <summary>Work exists, no objective check can grade it, and no review has been produced either — so no amount of further production can produce evidence, and a review is the only evidence available. Guarded on the review NOT existing yet: that is this row's exit, and without it an approving reviewer would simply buy another reviewer.</summary>
    private static bool NothingCanGradeTheWorkYet(QualityDecisionInput f) => !AnObjectiveCheckCanGrade(f) && WorkHasBeenAttempted(f) && !f.IndependentReviewRecorded;

    /// <summary>
    /// A check PASSED and other recorded evidence disagrees with it — the producer's self-claim contradicted it, an
    /// independent reviewer disapproved, or a recorded score sits below the confident floor.
    ///
    /// <para>Guarded on the pass, because "the evidence disagrees" is only a reason to buy MORE evidence while some
    /// of it still says the work is good. Once the check itself says Failed, the check and the disapproving
    /// reviewer AGREE — buying a third opinion on work two recorded signals already reject spends the increment on
    /// confirming what is known, so that shape falls through to the rows that change the work instead. The
    /// contradiction carrier that reaches this row is therefore the UNDER-claim shape (the producer reported failure
    /// on work the check passed); an over-claim's check failed, and is handled by the failure rows below.</para>
    /// </summary>
    private static bool ThePassedCheckIsDisputed(QualityDecisionInput f) => TheCheckPassed(f) && !NothingDisputesTheEvidence(f);

    /// <summary>An objective check WAS DECLARED, and it ran and passed. Reached only after the disputed row declined, so nothing recorded disagrees with it. Guarded on <see cref="AnObjectiveCheckCanGrade"/> so a Passed disposition recorded with no declared check — a contradictory input this record's own shape cannot rule out — reads as ungraded work instead of quietly borrowing a "declared check" reason it did not earn.</summary>
    private static bool TheCheckPassed(QualityDecisionInput f) => AnObjectiveCheckCanGrade(f) && f.LatestDisposition == VerificationDisposition.Passed;

    /// <summary>The check keeps failing on work whose recorded scale already has seams to split along.</summary>
    private static bool FailureRepeatsAtScale(QualityDecisionInput f) => FailureRepeats(f) && IsLargeScale(f);

    /// <summary>The check keeps failing — the current mechanism reproduces one outcome, so repeating it unchanged is the one thing the evidence rules out. NOT a claim that the failures are the same failure: nothing records which failure each attempt was.</summary>
    private static bool FailureRepeats(QualityDecisionInput f) => f.ConsecutiveFailedVerdicts >= RepeatedFailureFloor;

    /// <summary>The ONLY evidence this work can have was bought, and it approves: nothing objective can grade the work, a review exists, and nothing recorded disputes it. The exit from the review-buying row — without it, ungraded work with an approving reviewer buys reviewers forever.</summary>
    private static bool TheOnlyAvailableEvidenceApproves(QualityDecisionInput f) => !AnObjectiveCheckCanGrade(f) && WorkHasBeenAttempted(f) && f.IndependentReviewRecorded && NothingDisputesTheEvidence(f);

    // ── Shared readings of the recorded facts ───────────────────────────────────────────────────────────

    /// <summary>
    /// Whether an objective check can GRADE this work: one was declared, and the recorded verdict is not
    /// <see cref="VerificationDisposition.NotApplicable"/>. <c>NotApplicable</c> reads as no declared check on
    /// purpose — it is the vacuous-grade classification, a check that returned nothing to judge the work by — so
    /// the work routes to the only evidence left (a review) instead of to a repair of a check that has nothing
    /// wrong with it.
    /// </summary>
    private static bool AnObjectiveCheckCanGrade(QualityDecisionInput f) => f.CheckDeclared && f.LatestDisposition != VerificationDisposition.NotApplicable;

    /// <summary>
    /// Whether anything has been attempted yet. The guard on every evidence-buying row, and the reason a FRESH unit
    /// falls through to the baseline: with nothing produced, there is no check to repair and nothing for a reviewer
    /// to read, so buying evidence before any work exists would spend the increment on reviewing nothing.
    /// </summary>
    private static bool WorkHasBeenAttempted(QualityDecisionInput f) => f.AttemptCount > 0;

    /// <summary>Whether every recorded signal agrees with the evidence in hand. A null score is ABSENCE, not doubt — the lifted comparison leaves it unmatched, so unscored work reads as undisputed.</summary>
    private static bool NothingDisputesTheEvidence(QualityDecisionInput f) =>
        !f.SelfClaimContradictedTheCheck && !f.IndependentReviewDisapproved && !(f.RecordedReviewScore < ConfidentReviewScoreFloor);

    /// <summary>Work big enough to split: at least the unit floor of independently-checkoutable units, or a diff past the small-diff ceiling.</summary>
    private static bool IsLargeScale(QualityDecisionInput f) => f.WorkspaceUnitCount >= SplitScaleUnitFloor || f.ChangedFileCount >= SplitScaleFileFloor;

    // ── Reasons: each cites the facts that matched, so no stop is ever unexplained ──────────────────────

    private static string ExhaustedBudgetReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"the recorded cap of {f.BudgetCapUsd} USD leaves {f.RemainingUsd} USD after {f.SpendSoFarUsd} USD spent, below the {f.EstimatedNextAttemptCostUsd} USD another attempt costs at the recorded rate{UndercountNote(f)}");

    /// <summary>Disclosed, never compensated for: an undercounted spend can only make this row fire LATER than it should, so the honest move is to say so in the evidence rather than pad the arithmetic.</summary>
    private static string UndercountNote(QualityDecisionInput f) =>
        f.SpendIsUndercounted ? " (the recorded spend is a known undercount, so the true remainder is smaller)" : "";

    private static string HumanRequiredReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"attempt {f.AttemptCount} recorded a {VerificationDisposition.HumanReviewRequired} verdict, which no automated mechanism may overrule");

    private static string NoProgressReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"{f.NoProgressDecisions} consecutive decision(s) produced no new result, reaching the recorded no-progress cap of {f.MaxNoProgressDecisions}");

    private static string MachineryFailedReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"attempt {f.AttemptCount} recorded a {VerificationDisposition.InfraUnknown} verdict — the machinery failed, not the work, and a stronger model cannot fix a check that could not run");

    private static string WaivedReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"attempt {f.AttemptCount} recorded a {VerificationDisposition.Waived} verdict — a human authorized forgoing verification for this work, which is an authorization to stop spending and never a recorded pass");

    private static string UnrunCheckReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"a check is declared but {f.AttemptCount} attempt(s) recorded no verdict, so no evidence exists to judge the work by yet");

    private static string NoCheckReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"no objective check can grade this work (declared: {f.CheckDeclared}, verdict {f.LatestDisposition}) and no independent review has been produced, so {f.AttemptCount} attempt(s) can produce no evidence — a review is the only evidence available");

    private static string DisputedEvidenceReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"a {VerificationDisposition.Passed} verdict that the recorded evidence disagrees with (self-claim contradicted the check: {f.SelfClaimContradictedTheCheck}, independent review disapproved: {f.IndependentReviewDisapproved}, recorded score: {f.RecordedReviewScore?.ToString() ?? "none"} against a floor of {ConfidentReviewScoreFloor})");

    private static string GoalMetReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"attempt {f.AttemptCount} recorded a {VerificationDisposition.Passed} verdict from a declared check and nothing recorded disputes it");

    private static string RepeatAtScaleReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"the check failed on {f.ConsecutiveFailedVerdicts} consecutive attempt(s) across {f.WorkspaceUnitCount} unit(s) and {f.ChangedFileCount} changed file(s), which already has seams to split along");

    private static string RepeatLocalizedReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"the check failed on {f.ConsecutiveFailedVerdicts} consecutive attempt(s) confined to {f.WorkspaceUnitCount} unit(s) and {f.ChangedFileCount} changed file(s) — too localized to split, so the capability is what changes");

    private static string ReviewApprovedReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"no objective check can grade this work, and the independent review that is its only available evidence approves it (recorded score: {f.RecordedReviewScore?.ToString() ?? "none"}) with nothing recorded disputing it");

    private static string NoBlockingEvidenceReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"nothing recorded rules out another ordinary attempt ({f.AttemptCount} so far, verdict {f.LatestDisposition}, {f.ConsecutiveFailedVerdicts} consecutive failed verdict(s)) — the cheapest mechanism is the baseline every other one must beat");

    /// <summary>One row of the policy table — a predicate over the recorded facts, the mechanism it chooses, and the author of that choice's evidence (Rule 18.1 — a pure data tuple, no behaviour beyond the delegates).</summary>
    private sealed record PolicyRow(Func<QualityDecisionInput, bool> Matches, QualityMechanism Mechanism, Func<QualityDecisionInput, string> Reason);
}
