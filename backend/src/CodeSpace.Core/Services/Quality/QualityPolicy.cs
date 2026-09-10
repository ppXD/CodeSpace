using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Failures;
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
/// reflection-enforced). So "which mechanism does this kind of task get?" is not a question this policy can be
/// asked — it only answers "what does the recorded evidence say the next increment should buy?". A new task
/// phrasing or repository shape needs no new case, because there is no case to add.</para>
///
/// <para><b>Ordering is soundness, not taste.</b> The affordability gate is FIRST because a mechanism nobody can
/// pay for is not an option; the infra row precedes the escalate/split rows so a broken check can never buy a
/// stronger model; the disputed-evidence row precedes the goal-met row so a check that passed while other recorded
/// evidence disagrees still buys an independent look before shipping. Each ordered row is pinned by its own theory
/// case in <c>QualityPolicyTests</c>, so a reorder is a test-visible decision rather than a silent behaviour change.</para>
///
/// <para><b>Every mechanism is provisional.</b> P22-9c's same-budget ablation harness measures each mechanism
/// against spending the same budget on more <see cref="QualityMechanism.SingleAgent"/> attempts; a row whose
/// mechanism never wins its ablation is DELETED rather than kept as an unmeasured option, and the three thresholds
/// below are the boundaries that ablation moves. P22-9b wires this policy into the supervisor / executor decision
/// points behind the P14 decider lease — until then nothing calls it and no production behaviour depends on it.</para>
/// </summary>
public static class QualityPolicy
{
    /// <summary>Recorded review scores at-or-above this (0–100) are not treated as uncertainty. A guess until P22-9c's ablation measures it; pinned by test so moving it is a deliberate edit.</summary>
    public const int ConfidentReviewScoreFloor = 70;

    /// <summary>How many identical work-classed failures make a REPEAT. Two: the second identical failure is the first repetition, and the first is simply a failure. Pinned by test.</summary>
    public const int RepeatedFailureFloor = 2;

    /// <summary>Changed-file count at-or-above which repeated failure prefers splitting over escalating — a conventional small-diff ceiling. A guess until P22-9c's ablation measures it; pinned by test.</summary>
    public const int SplitScaleFileFloor = 10;

    /// <summary>
    /// The mechanism the recorded evidence chooses, with the evidence that chose it. First matching row wins; the
    /// catch-all guarantees a result. Every stop reports <c>0</c> expected marginal value (no further spend can
    /// change the outcome) and every other mechanism reports <c>null</c> — unknown until 9c measures it, never an
    /// invented estimate.
    /// </summary>
    public static QualityDecision Decide(QualityDecisionInput facts)
    {
        var row = Rows.First(r => r.Matches(facts));

        return new QualityDecision
        {
            Mechanism = row.Mechanism,
            Reason = row.Reason(facts),
            ExpectedMarginalValue = row.Mechanism == QualityMechanism.Stop ? 0d : null,
        };
    }

    /// <summary>
    /// The ORDERED rule table — first match wins, read top-to-bottom as "what does the evidence already rule out?".
    /// The two zero-cost exits and the evidence-buying mechanisms come before the spending ones; the last row's
    /// <c>_ => true</c> predicate makes the table total.
    /// </summary>
    private static readonly IReadOnlyList<PolicyRow> Rows = new[]
    {
        new PolicyRow(CannotAffordAnotherAttempt, QualityMechanism.Stop, ExhaustedBudgetReason),
        new PolicyRow(AHumanVerdictIsRequired, QualityMechanism.AskHuman, HumanRequiredReason),
        new PolicyRow(NoProgressCapIsReached, QualityMechanism.Stop, NoProgressReason),
        new PolicyRow(TheFailureIsInfraClassed, QualityMechanism.BoundedRepair, InfraFailureReason),
        new PolicyRow(ADeclaredCheckNeverRan, QualityMechanism.BoundedRepair, UnrunCheckReason),
        new PolicyRow(NoCheckWasDeclared, QualityMechanism.IndependentCritic, NoCheckReason),
        new PolicyRow(TheRecordedEvidenceIsDisputed, QualityMechanism.IndependentCritic, DisputedEvidenceReason),
        new PolicyRow(TheCheckPassed, QualityMechanism.Stop, GoalMetReason),
        new PolicyRow(WorkFailureRepeatsAtScale, QualityMechanism.SplitIntoSubtasks, RepeatAtScaleReason),
        new PolicyRow(WorkFailureRepeats, QualityMechanism.EscalateModel, RepeatLocalizedReason),
        new PolicyRow(_ => true, QualityMechanism.SingleAgent, NoBlockingEvidenceReason),
    };

    // ── Predicates: each reads recorded facts only ──────────────────────────────────────────────────────

    /// <summary>The recorded remainder cannot pay for one more attempt at the recorded rate. Uncapped work, or work whose next-attempt cost is unrecorded, can never match — the policy stops for budget only on evidence, never on suspicion.</summary>
    private static bool CannotAffordAnotherAttempt(QualityDecisionInput f) => f.RemainingUsd is { } remaining && f.EstimatedNextAttemptCostUsd is { } next && remaining < next;

    /// <summary>A recorded verdict says a HUMAN must decide. The only route to <see cref="QualityMechanism.AskHuman"/> — it is never a generic fallback for work the table found hard.</summary>
    private static bool AHumanVerdictIsRequired(QualityDecisionInput f) => f.LatestDisposition == VerificationDisposition.HumanReviewRequired;

    /// <summary>The recorded consecutive-no-progress count reached its recorded cap. Unmatched when no cap is recorded.</summary>
    private static bool NoProgressCapIsReached(QualityDecisionInput f) => f.MaxNoProgressDecisions is { } cap && f.NoProgressDecisions >= cap;

    /// <summary>The MACHINERY failed, not the work — an infra-classed verdict, or a failure kind that names a dependency, a quota, or a broken invariant. Placed before the escalate/split rows so a stronger model is never bought to fix a broken check.</summary>
    private static bool TheFailureIsInfraClassed(QualityDecisionInput f) =>
        f.LatestDisposition == VerificationDisposition.InfraUnknown || f.LatestFailure is FailureKind.Unavailable or FailureKind.Exhausted or FailureKind.Internal;

    /// <summary>A check WAS declared, work HAS been attempted, and there is still no verdict — the evidence the work was supposed to produce never got produced.</summary>
    private static bool ADeclaredCheckNeverRan(QualityDecisionInput f) => f.CheckDeclared && WorkHasBeenAttempted(f) && f.LatestDisposition == VerificationDisposition.Unknown;

    /// <summary>Work exists and no objective check can grade it, so no amount of further production can produce evidence — a review is the only evidence available.</summary>
    private static bool NoCheckWasDeclared(QualityDecisionInput f) => !f.CheckDeclared && WorkHasBeenAttempted(f);

    /// <summary>
    /// Whether anything has been attempted yet. The guard on both evidence-buying rows, and the reason a FRESH unit
    /// falls through to the baseline: with nothing produced, there is no check to repair and nothing for a reviewer
    /// to read, so buying evidence before any work exists would spend the increment on reviewing nothing.
    /// </summary>
    private static bool WorkHasBeenAttempted(QualityDecisionInput f) => f.AttemptCount > 0;

    /// <summary>Recorded evidence DISAGREES with itself: the producer's self-claim contradicted the check, an independent reviewer disapproved, or a recorded score sits below the confident floor. A null score is absence, not doubt — the lifted comparison leaves it unmatched.</summary>
    private static bool TheRecordedEvidenceIsDisputed(QualityDecisionInput f) =>
        f.SelfClaimContradictedTheCheck || f.IndependentReviewDisapproved || f.RecordedReviewScore < ConfidentReviewScoreFloor;

    /// <summary>The declared check ran and passed. Reached only after the disputed row declined, so nothing recorded disagrees with it.</summary>
    private static bool TheCheckPassed(QualityDecisionInput f) => f.LatestDisposition == VerificationDisposition.Passed;

    /// <summary>The same work-classed failure repeats on work whose recorded scale already has seams to split along.</summary>
    private static bool WorkFailureRepeatsAtScale(QualityDecisionInput f) => WorkFailureRepeats(f) && IsLargeScale(f);

    /// <summary>The same work-classed failure repeats — the current mechanism keeps reproducing one outcome, so repeating it again is the one thing evidence rules out.</summary>
    private static bool WorkFailureRepeats(QualityDecisionInput f) => f.IdenticalWorkFailureStreak >= RepeatedFailureFloor;

    /// <summary>Work big enough to split: more than one independently-checkoutable unit, or a diff past the small-diff ceiling.</summary>
    private static bool IsLargeScale(QualityDecisionInput f) => f.WorkspaceUnitCount > 1 || f.ChangedFileCount >= SplitScaleFileFloor;

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

    private static string InfraFailureReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"attempt {f.AttemptCount} failed the machinery, not the work (verdict {f.LatestDisposition}, failure kind {f.LatestFailure?.ToString() ?? "unclassified"}) — a stronger model cannot fix a check that could not run");

    private static string UnrunCheckReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"a check is declared but {f.AttemptCount} attempt(s) recorded no verdict, so no evidence exists to judge the work by yet");

    private static string NoCheckReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"no objective check is declared, so {f.AttemptCount} attempt(s) can produce no verdict — an independent review is the only evidence available");

    private static string DisputedEvidenceReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"the recorded evidence disagrees with itself (self-claim contradicted the check: {f.SelfClaimContradictedTheCheck}, independent review disapproved: {f.IndependentReviewDisapproved}, recorded score: {f.RecordedReviewScore?.ToString() ?? "none"} against a floor of {ConfidentReviewScoreFloor})");

    private static string GoalMetReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"attempt {f.AttemptCount} recorded a {VerificationDisposition.Passed} verdict from a declared check and nothing recorded disputes it");

    private static string RepeatAtScaleReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"{f.IdenticalWorkFailureStreak} consecutive attempt(s) failed the work the same way ({f.LatestFailure?.ToString() ?? "unclassified"}) across {f.WorkspaceUnitCount} unit(s) and {f.ChangedFileCount} changed file(s), which already has seams to split along");

    private static string RepeatLocalizedReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"{f.IdenticalWorkFailureStreak} consecutive attempt(s) failed the work the same way ({f.LatestFailure?.ToString() ?? "unclassified"}) on {f.ChangedFileCount} changed file(s) — too localized to split, so the capability is what changes");

    private static string NoBlockingEvidenceReason(QualityDecisionInput f) =>
        FormattableString.Invariant($"nothing recorded rules out another ordinary attempt ({f.AttemptCount} so far, verdict {f.LatestDisposition}, {f.IdenticalWorkFailureStreak} identical work failure(s)) — the cheapest mechanism is the baseline every other one must beat");

    /// <summary>One row of the policy table — a predicate over the recorded facts, the mechanism it chooses, and the author of that choice's evidence (Rule 18.1 — a pure data tuple, no behaviour beyond the delegates).</summary>
    private sealed record PolicyRow(Func<QualityDecisionInput, bool> Matches, QualityMechanism Mechanism, Func<QualityDecisionInput, string> Reason);
}
