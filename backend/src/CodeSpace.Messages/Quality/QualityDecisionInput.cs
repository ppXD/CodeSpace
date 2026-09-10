using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Quality;

/// <summary>
/// The COMPLETE fact surface <c>QualityPolicy</c> decides from (P22, Rule 18.1 — a data noun). Every member is a
/// fact the platform ALREADY RECORDS, reduced to a number, a bool, or a typed classification — with one named
/// exception: <see cref="EstimatedNextAttemptCostUsd"/> is CALLER-DERIVED from recorded costs, not itself a single
/// recorded figure (see its own doc).
///
/// <para><b>PURITY INVARIANT (the P22 refutation, enforced — <c>QualityDecisionInputPurityTests</c>):</b> the
/// carried types are an ALLOW-LIST (<c>bool</c>, <c>int</c>, <c>decimal</c>, <see cref="VerificationDisposition"/>,
/// <see cref="QualityAttemptFact"/>) checked over the whole transitive shape, and no member name — nor any carried
/// type name — contains an identity noun (Task / Goal / Provider / Model / Repo / Path / Name). An allow-list
/// rather than a ban on <c>string</c> and <c>Guid</c> on purpose: a deny-list only ever refuses the identity
/// carriers somebody already thought of, and the interesting ones (a <c>Uri</c>, a <c>byte[]</c> digest, a
/// nested options record) are exactly the ones nobody lists. So the policy CANNOT switch on what the task is
/// called, which provider or model ran it, which repository it touched, or where an artifact lives — those facts
/// are not merely unused, they are UNREPRESENTABLE here, and a new task phrasing / repository shape / novel
/// artifact path cannot fail for want of a switch-case because there is no switch to add a case to.</para>
///
/// <para>Scale members are named for the SCALE AXIS rather than the entity (<see cref="WorkspaceUnitCount"/>, not
/// "repository count") so the identity-noun guard can stay a TOTAL rule over names with no exemption list — an
/// exemption is exactly the crack a future identity field would slip through. A count of things is scale; the
/// NAME of a thing is identity, and only the latter is banned.</para>
///
/// <para>A new signal is an ADDITIVE member (defaulting to the reading that changes no existing decision) plus a
/// new ordered row in the policy table — never a widening of an existing member's meaning (Rule 7).</para>
///
/// <para><b>Grains 9b must pin (they are NOT decided here).</b> Two members have more than one recorded grain and
/// this record deliberately takes no position on which one a call site should read; picking wrongly is a silent
/// mis-stop, so each is called out on the member itself: <see cref="SpendSoFarUsd"/> (a run's spend versus a unit's)
/// and <see cref="BudgetCapUsd"/> (<c>AgentTask.MaxCostUsd</c>, per task, versus <c>SupervisorGoalPlan.MaxCostUsd</c>,
/// per run).</para>
/// </summary>
public sealed record QualityDecisionInput
{
    // ── Budget: what has been spent, what the cap is, and what one more attempt would cost ───────────────

    /// <summary>
    /// Realized spend in USD attributable to the work this decision is about. Zero when nothing has been spent.
    /// <para>GRAIN WARNING for 9b: the closest recorded figure, <c>SupervisorTurnContext.RunSpendUsd</c>, is the
    /// WHOLE RUN's realized spend (brain-plane plus every spawned agent), not one unit's — reading it for a
    /// per-unit decision makes every unit in a fan-out look as expensive as all of them together, which would stop
    /// the second sibling for the first sibling's bill. A per-unit read has to be folded from the attempts' own
    /// <c>AgentRunResult.CostUsd</c>, and 9b must state which grain it passes.</para>
    /// </summary>
    public decimal SpendSoFarUsd { get; init; }

    /// <summary>
    /// The recorded cost cap in USD, or null when the work is UNCAPPED. Null means the affordability row can never
    /// fire — an uncapped run is never stopped for budget.
    /// <para>GRAIN WARNING for 9b: two caps are recorded and they are not the same number —
    /// <c>AgentTask.MaxCostUsd</c> (this task's own cap) and <c>SupervisorGoalPlan.MaxCostUsd</c> (the whole run's,
    /// as carried on <c>SupervisorTurnContext.MaxCostUsd</c>). It must be paired with a <see cref="SpendSoFarUsd"/>
    /// of the same grain, or <see cref="RemainingUsd"/> subtracts one thing from another.</para>
    /// </summary>
    public decimal? BudgetCapUsd { get; init; }

    /// <summary>
    /// NOT itself a recorded fact — the one exception to the class doc's claim: what ONE more attempt would cost in
    /// USD, derived by the caller from the RECORDED cost of the attempts already made (<c>AgentRunResult.CostUsd</c>).
    /// Null when no attempt has a priced cost yet — nothing recorded then says what another attempt costs, so the
    /// affordability row cannot fire and the policy must not guess.
    /// <para>UNSPECIFIED HERE, deliberately: the estimator (last attempt's cost? the mean? the max? the plan's
    /// cap ÷ its spawn budget, as the supervisor's own reservation slice does?) is a 9b decision with real
    /// consequences — a mean over a cheap first attempt under-stops, a max over one expensive outlier over-stops.
    /// This record only requires that whatever is passed came from recorded costs.</para>
    /// </summary>
    public decimal? EstimatedNextAttemptCostUsd { get; init; }

    /// <summary>
    /// The HONESTY QUALIFIER on <see cref="SpendSoFarUsd"/>: true when the recorded spend is a known UNDERCOUNT —
    /// the recorded <c>SupervisorTurnContext.UnpricedSpendModel</c> (a model this run already spent on that nobody
    /// can price) or <c>AgentRunResult.CostIndeterminate</c>. It rides the decision's reason for audit and
    /// deliberately changes NO mechanism: an undercount can only ever make the policy UNDER-stop (the true
    /// remainder is smaller than the computed one), never over-stop, so it needs no compensating fudge — only
    /// disclosure. Pinned by test.
    /// <para>REACHABILITY for 9b to confirm before relying on it: this qualifier is pre-empted UPSTREAM of any
    /// quality decision only when grain and signal align. <c>SupervisorBounds</c> force-stops when a PLAN-level cap
    /// (<c>SupervisorGoalPlan.MaxCostUsd</c>) coexists with an unpriced spend at that same grain
    /// (<c>SupervisorTurnContext.UnpricedSpendModel</c>, folded from <c>SupervisorOutcome.FirstUnpricedModel</c> —
    /// NOT <c>CostIndeterminate</c>); <c>AgentRunExecutor.CostBudgetStopsFurtherCalls</c> separately stops further
    /// calls WITHIN one run when a TASK-level cap (<c>AgentTask.MaxCostUsd</c>) coexists with that run's own
    /// <c>CostIndeterminate</c> result. Every other grain/signal combination reaches this row unobstructed: a
    /// task-level cap paired with an unpriced model, a plan-level cap paired with a priced model's
    /// <c>CostIndeterminate</c> result, and a capped task running under an uncapped plan are all reachable. That is
    /// an argument for confirming which grain 9b reads, not for assuming the qualifier is dead weight.</para>
    /// </summary>
    public bool SpendIsUndercounted { get; init; }

    /// <summary>Budget left in USD, or null when uncapped. Clamped at zero — an overspend reads as nothing left, never as a negative allowance.</summary>
    public decimal? RemainingUsd => BudgetCapUsd is { } cap ? decimal.Max(0m, cap - SpendSoFarUsd) : null;

    // ── Attempts already made, as classifications only ──────────────────────────────────────────────────

    /// <summary>Every attempt already made, oldest first, each reduced to its recorded verdict. Empty = nothing has been attempted yet.</summary>
    public IReadOnlyList<QualityAttemptFact> Attempts { get; init; } = Array.Empty<QualityAttemptFact>();

    /// <summary>How many attempts have been made.</summary>
    public int AttemptCount => Attempts.Count;

    /// <summary>The most recent attempt's verification verdict, or <see cref="VerificationDisposition.Unknown"/> when nothing has been attempted. A caller holding a nullable <c>AcceptanceVerdict</c> maps null to <see cref="VerificationDisposition.Unknown"/> — the SAME mapping <c>VerificationDispositions.Classify</c> already makes, so 9b cannot invent a second one.</summary>
    public VerificationDisposition LatestDisposition => Attempts.Count == 0 ? VerificationDisposition.Unknown : Attempts[^1].Disposition;

    /// <summary>
    /// How many attempts, counting back from the NEWEST, recorded a <see cref="VerificationDisposition.Failed"/>
    /// verdict — the "the check keeps failing" evidence. Zero unless the newest attempt is itself a
    /// <c>Failed</c>, so any other tail (a pass, a waiver, an ungraded attempt, or an
    /// <see cref="VerificationDisposition.InfraUnknown"/> one) breaks the run rather than extending it.
    ///
    /// <para>Deliberately NOT "failed the same way": <c>Failed</c> is <c>Classify</c>'s work-classed arm (its
    /// infra-classed arm is the separate <c>InfraUnknown</c>), so a run of them is a run of work-classed failures —
    /// but nothing records WHICH failure each one was (see <see cref="QualityAttemptFact"/>), so two consecutive
    /// failures being the SAME failure is not decidable today. The weaker claim is the one the recorded facts
    /// support, and it is the one the reason text states.</para>
    /// </summary>
    public int ConsecutiveFailedVerdicts => Attempts.Reverse().TakeWhile(a => a.Disposition == VerificationDisposition.Failed).Count();

    // ── No progress: the recorded stuck-ness counter and its recorded cap ───────────────────────────────

    /// <summary>Consecutive decisions that produced no new settled result (the recorded <c>SupervisorTurnContext.NoProgressDecisions</c>).</summary>
    public int NoProgressDecisions { get; init; }

    /// <summary>The recorded no-progress cap (the recorded <c>SupervisorTurnContext.MaxNoProgressDecisions</c>), or null when no cap is recorded — the no-progress row then cannot fire.</summary>
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

    /// <summary>
    /// Whether an independent review was actually PRODUCED for this work: a <c>CriticVerdict</c> exists whose
    /// <c>Failed</c> is false. A FAILED review (no reviewer model, a call or parse failure) is NOT a recorded
    /// review — its caller falls back to the producer's original output, so it bought nothing and must not read as
    /// evidence. This is what gives the "no objective check can grade it" row an EXIT: without it that row re-buys
    /// a reviewer for the same ungraded work forever, however approvingly the last one answered.
    /// </summary>
    public bool IndependentReviewRecorded { get; init; }

    /// <summary>
    /// Whether an independent reviewer DISAPPROVED (the recorded, severity-authoritative <c>CriticVerdict.Approved</c>
    /// as computed by <c>CriticGatePolicy</c> — never a re-interpretation of the issue list here).
    /// <para>FOR 9b: this must be mapped as <c>!verdict.Approved &amp;&amp; !verdict.Failed</c>. A failed review also
    /// carries <c>Approved == false</c> (<c>CriticVerdict.ReviewFailed</c> sets both), so mapping from
    /// <c>Approved</c> alone turns "the reviewer could not be reached" into "the reviewer disapproved" — a
    /// fabricated verdict, and one that would dispute a passing check.</para>
    /// </summary>
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
