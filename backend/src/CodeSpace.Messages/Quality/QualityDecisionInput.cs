using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Quality;

/// <summary>
/// The COMPLETE fact surface <c>QualityPolicy</c> decides from (P22, Rule 18.1 — a data noun). Every member is a
/// fact the platform ALREADY RECORDS, reduced to a number, a bool, or a typed classification.
///
/// <para><b>PURITY INVARIANT (the P22 refutation, enforced — <c>QualityDecisionInputPurityTests</c>):</b> this
/// record's transitive shape carries NO <c>string</c>, no string collection, and no <c>Guid</c>, and no member
/// name contains an identity noun (Task / Goal / Provider / Model / Repo / Path / Name). So the policy CANNOT
/// switch on what the task is called, which provider or model ran it, which repository it touched, or where an
/// artifact lives — those facts are not merely unused, they are UNREPRESENTABLE here. A new task phrasing, a new
/// repository shape, or a novel artifact path therefore cannot fail for want of a switch-case: there is no switch
/// to add a case to. That is why the guard is a reflection test over the shape rather than a review convention —
/// adding <c>string TaskKind</c> to earn one special case breaks the build's test run, in the open.</para>
///
/// <para>Scale members are named for the SCALE AXIS rather than the entity (<see cref="WorkspaceUnitCount"/>, not
/// "repository count") so the identity-noun guard can stay a TOTAL rule over member names with no exemption list —
/// an exemption is exactly the crack a future identity field would slip through. A count of things is scale; the
/// NAME of a thing is identity, and only the latter is banned.</para>
///
/// <para>A new signal is an ADDITIVE member (defaulting to the reading that changes no existing decision) plus a
/// new ordered row in the policy table — never a widening of an existing member's meaning (Rule 7).</para>
/// </summary>
public sealed record QualityDecisionInput
{
    // ── Budget: what has been spent, what the cap is, and what one more attempt would cost ───────────────

    /// <summary>Realized spend on this unit of work so far, in USD (the recorded <c>SupervisorTurnContext.RunSpendUsd</c> grain). Zero when nothing has been spent.</summary>
    public decimal SpendSoFarUsd { get; init; }

    /// <summary>The recorded cost cap in USD, or null when the work is UNCAPPED (the recorded <c>MaxCostUsd</c> convention). Null means the affordability row can never fire — an uncapped run is never stopped for budget.</summary>
    public decimal? BudgetCapUsd { get; init; }

    /// <summary>
    /// What ONE more attempt would cost in USD, derived by the caller from the RECORDED cost of the attempts
    /// already made (<c>AgentRunResult.CostUsd</c>). Null when no attempt has a priced cost yet — nothing recorded
    /// then says what another attempt costs, so the affordability row cannot fire and the policy must not guess.
    /// </summary>
    public decimal? EstimatedNextAttemptCostUsd { get; init; }

    /// <summary>
    /// The HONESTY QUALIFIER on <see cref="SpendSoFarUsd"/>: true when the recorded spend is a known UNDERCOUNT
    /// (the recorded <c>CostIndeterminate</c> / <c>UnpricedSpendModel</c> conditions — a model call the price book
    /// could not price). It rides the decision's reason for audit and deliberately changes NO mechanism: an
    /// undercount can only ever make the policy UNDER-stop (true remaining is smaller than computed), never
    /// over-stop, so it needs no compensating fudge — only disclosure. Pinned by test.
    /// </summary>
    public bool SpendIsUndercounted { get; init; }

    /// <summary>Budget left in USD, or null when uncapped. Clamped at zero — an overspend reads as nothing left, never as a negative allowance.</summary>
    public decimal? RemainingUsd => BudgetCapUsd is { } cap ? decimal.Max(0m, cap - SpendSoFarUsd) : null;

    // ── Attempts already made, as classifications only ──────────────────────────────────────────────────

    /// <summary>Every attempt already made, oldest first, each reduced to its two recorded classifications. Empty = nothing has been attempted yet.</summary>
    public IReadOnlyList<QualityAttemptFact> Attempts { get; init; } = Array.Empty<QualityAttemptFact>();

    /// <summary>How many attempts have been made.</summary>
    public int AttemptCount => Attempts.Count;

    /// <summary>The most recent attempt's verification verdict, or <see cref="VerificationDisposition.Unknown"/> when nothing has been attempted. A caller holding a nullable <c>AcceptanceVerdict</c> maps null to <see cref="VerificationDisposition.Unknown"/> — the SAME mapping <c>VerificationDispositions.Classify</c> already makes, so 9b cannot invent a second one.</summary>
    public VerificationDisposition LatestDisposition => Attempts.Count == 0 ? VerificationDisposition.Unknown : Attempts[^1].Disposition;

    /// <summary>The most recent attempt's recorded failure kind, or null when it did not fail / nothing was attempted.</summary>
    public FailureKind? LatestFailure => Attempts.Count == 0 ? null : Attempts[^1].Failure;

    /// <summary>
    /// How many attempts, counting back from the newest, failed the WORK the SAME way — the same
    /// <see cref="VerificationDisposition.Failed"/> verdict AND the same <see cref="QualityAttemptFact.Failure"/>
    /// kind. Zero unless the newest attempt is itself a work-classed failure, so an infra-classed or ungraded tail
    /// never accumulates a repeat streak. This is the "the same thing keeps happening" evidence, decided from two
    /// enums — no failure message, no fingerprint string (see the purity invariant).
    /// </summary>
    public int IdenticalWorkFailureStreak => Attempts.Count > 0 && Attempts[^1].Disposition == VerificationDisposition.Failed
        ? Attempts.Reverse().TakeWhile(a => a.Disposition == VerificationDisposition.Failed && a.Failure == Attempts[^1].Failure).Count()
        : 0;

    // ── No progress: the recorded stuck-ness counter and its recorded cap ───────────────────────────────

    /// <summary>Consecutive decisions that produced no new settled result (the recorded <c>SupervisorTurnContext.NoProgressDecisions</c>).</summary>
    public int NoProgressDecisions { get; init; }

    /// <summary>The recorded no-progress cap (the recorded <c>MaxNoProgressDecisions</c>), or null when no cap is recorded — the no-progress row then cannot fire.</summary>
    public int? MaxNoProgressDecisions { get; init; }

    // ── Whether evidence exists at all ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether an objective check was DECLARED for this work (the recorded <c>AgentAcceptanceContract.RequiresGrade</c>
    /// condition — an acceptance spec is present). Separate from <see cref="LatestDisposition"/> ON PURPOSE: the
    /// recorded <c>bool? AcceptancePassed</c> conflates "no check exists" with "a check exists but never ran", and
    /// those want OPPOSITE mechanisms — buy a review versus repair the check. Two facts can tell them apart; one
    /// tri-state cannot.
    /// </summary>
    public bool CheckDeclared { get; init; }

    // ── Uncertainty proxies, as actually recorded today ─────────────────────────────────────────────────

    /// <summary>
    /// Whether the producer's own SELF-CLAIM disagreed with the objective check (the recorded
    /// <c>AgentContradiction</c> over-claim / under-claim). This is the system's one per-unit disagreement fact and
    /// the same evidence <c>SupervisorRetryEscalation</c> already escalates on. False also when no check existed to
    /// disagree with — that case is carried by <see cref="CheckDeclared"/>, whose row runs first.
    /// </summary>
    public bool SelfClaimContradictedTheCheck { get; init; }

    /// <summary>Whether an independent reviewer DISAPPROVED (the recorded, severity-authoritative <c>CriticVerdict.Approved</c> as computed by <c>CriticGatePolicy</c> — never a re-interpretation of the issue list here).</summary>
    public bool IndependentReviewDisapproved { get; init; }

    /// <summary>
    /// The recorded review score, 0–100, or null when nothing scored the work (the recorded
    /// <c>CriticVerdict.Score</c>). NOTE: the platform records NO per-verdict confidence number today — the rubric
    /// judge is deliberately binary — so this score is the only recorded numeric uncertainty proxy, and it is
    /// carried under its real name rather than dressed up as a confidence the system does not have. A null is
    /// ABSENCE of a score, never a low one, so it never on its own reads as uncertainty (pinned by test).
    /// </summary>
    public int? RecordedReviewScore { get; init; }

    // ── Scale: how big the work is, as counts ───────────────────────────────────────────────────────────

    /// <summary>Files changed by the work so far (the recorded <c>PublishManifest.ChangedFileCount</c>). A SCALE, not a set of paths.</summary>
    public int ChangedFileCount { get; init; }

    /// <summary>
    /// How many independently-checkoutable units the workspace holds — today the count of the recorded
    /// <c>WorkspaceSpec.Repositories</c>. Named for the scale axis, not the entity, so the identity-noun guard
    /// stays a total rule with no exemptions. More than one unit is the clearest split candidate there is: the work
    /// already has seams the plan did not have to invent.
    /// </summary>
    public int WorkspaceUnitCount { get; init; }
}
