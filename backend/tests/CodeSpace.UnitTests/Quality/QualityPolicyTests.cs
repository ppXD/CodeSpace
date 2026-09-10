using CodeSpace.Core.Services.Quality;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Quality;
using Shouldly;

namespace CodeSpace.UnitTests.Quality;

/// <summary>
/// Pins the PURE adaptive quality policy (P22-9a) — the ordered rule table that chooses a mechanism from RECORDED
/// evidence. Three kinds of pin, deliberately separated: <see cref="RuleTable"/> pins that each row EXISTS and
/// cites its own facts; <see cref="Orderings"/> pins, for every pair of rows that can collide on one input, WHICH
/// one wins; and the remaining tests pin the boundaries and the totality of the table. A row's existence and a
/// row's precedence are different claims, and a table that only pins the former reorders silently.
/// </summary>
[Trait("Category", "Unit")]
public sealed class QualityPolicyTests
{
    /// <summary>
    /// One case per ORDERED ROW, in table order, each asserting both the mechanism and the fact-citing evidence.
    /// The cases are built as whole fact records rather than a wide InlineData: naming only the facts that matter
    /// is what makes each row's trigger readable. PRECEDENCE is not tested here — see <see cref="Orderings"/>.
    /// </summary>
    public static TheoryData<string, QualityDecisionInput, QualityMechanism, string> RuleTable() => new()
    {
        // Row 1 — the recorded remainder cannot buy another attempt. FIRST, because a mechanism nobody can pay for is not an option.
        {
            "budget below the cost of another attempt",
            new QualityDecisionInput { BudgetCapUsd = 1.00m, SpendSoFarUsd = 0.95m, EstimatedNextAttemptCostUsd = 0.20m, CheckDeclared = true },
            QualityMechanism.Stop, "leaves 0.05 USD after 0.95 USD spent, below the 0.20 USD another attempt costs"
        },
        // Row 2 — a recorded verdict says a human must decide. The ONLY route to AskHuman.
        {
            "a recorded human-review verdict",
            Attempted(VerificationDisposition.HumanReviewRequired) with { CheckDeclared = true },
            QualityMechanism.AskHuman, "which no automated mechanism may overrule"
        },
        // Row 3 — the recorded no-progress counter reached its recorded cap.
        {
            "the recorded no-progress cap is reached",
            new QualityDecisionInput { NoProgressDecisions = 8, MaxNoProgressDecisions = 8, CheckDeclared = true },
            QualityMechanism.Stop, "reaching the recorded no-progress cap of 8"
        },
        // Row 4 — the check MACHINERY failed. The one recorded classification that says so, and never a stronger model.
        {
            "an infra-classed verdict",
            Attempted(VerificationDisposition.InfraUnknown) with { CheckDeclared = true },
            QualityMechanism.BoundedRepair, "the machinery failed, not the work, and a stronger model cannot fix a check that could not run"
        },
        // Row 5 — a HUMAN waived verification. A stop, and emphatically not a recorded pass.
        {
            "a human-waived verdict",
            Attempted(VerificationDisposition.Waived) with { CheckDeclared = true },
            QualityMechanism.Stop, "a human authorized forgoing verification for this work, which is an authorization to stop spending and never a recorded pass"
        },
        // Row 6 — a check was declared, work was attempted, and no verdict came back.
        {
            "a declared check that never ran",
            Attempted(VerificationDisposition.Unknown) with { CheckDeclared = true },
            QualityMechanism.BoundedRepair, "a check is declared but 1 attempt(s) recorded no verdict"
        },
        // Row 7 — work exists that nothing objective can grade, and no review has been bought yet.
        {
            "no check declared and no review yet for work already produced",
            Attempted(VerificationDisposition.Unknown) with { CheckDeclared = false },
            QualityMechanism.IndependentCritic, "no independent review has been produced"
        },
        // Row 8 — a PASSED check the recorded evidence disagrees with, by each of its three recorded carriers.
        {
            "a passed check the self-claim contradicted",
            Attempted(VerificationDisposition.Passed) with { CheckDeclared = true, SelfClaimContradictedTheCheck = true },
            QualityMechanism.IndependentCritic, "self-claim contradicted the check: True"
        },
        {
            "a passed check an independent reviewer disapproved",
            Attempted(VerificationDisposition.Passed) with { CheckDeclared = true, IndependentReviewRecorded = true, IndependentReviewDisapproved = true },
            QualityMechanism.IndependentCritic, "independent review disapproved: True"
        },
        {
            "a passed check with a recorded score below the confident floor",
            Attempted(VerificationDisposition.Passed) with { CheckDeclared = true, IndependentReviewRecorded = true, RecordedReviewScore = 40 },
            QualityMechanism.IndependentCritic, "recorded score: 40 against a floor of 70"
        },
        // Row 9 — the declared check passed and nothing recorded disputes it.
        {
            "the declared check passed undisputed",
            Attempted(VerificationDisposition.Passed) with { CheckDeclared = true },
            QualityMechanism.Stop, "nothing recorded disputes it"
        },
        // Row 10 — the check keeps failing on work that already has seams: split, don't escalate.
        {
            "a repeated failure across several units",
            Failed(2) with { CheckDeclared = true, WorkspaceUnitCount = 3, ChangedFileCount = 2 },
            QualityMechanism.SplitIntoSubtasks, "already has seams to split along"
        },
        {
            "a repeated failure across a wide diff",
            Failed(2) with { CheckDeclared = true, WorkspaceUnitCount = 1, ChangedFileCount = 10 },
            QualityMechanism.SplitIntoSubtasks, "across 1 unit(s) and 10 changed file(s)"
        },
        // Row 11 — the check keeps failing on work too localized to split: the capability changes instead.
        {
            "a repeated failure on a localized diff",
            Failed(2) with { CheckDeclared = true, WorkspaceUnitCount = 1, ChangedFileCount = 3 },
            QualityMechanism.EscalateModel, "too localized to split, so the capability is what changes"
        },
        // Row 12 — the review that is the ungraded work's only available evidence approves it. Row 7's EXIT.
        {
            "the only available evidence approves the ungraded work",
            Attempted(VerificationDisposition.Unknown) with { CheckDeclared = false, IndependentReviewRecorded = true, RecordedReviewScore = 88 },
            QualityMechanism.Stop, "the independent review that is its only available evidence approves it"
        },
        // Row 13 (catch-all) — a single failure is not yet a repeat, and a fresh unit has nothing to review.
        {
            "one failed verdict, not yet a repeat",
            Failed(1) with { CheckDeclared = true },
            QualityMechanism.SingleAgent, "nothing recorded rules out another ordinary attempt"
        },
        {
            "a fresh unit with nothing recorded at all",
            new QualityDecisionInput(),
            QualityMechanism.SingleAgent, "the cheapest mechanism is the baseline"
        },
        // The REFUTATION row: a fact shape no case above enumerates — a 47-unit workspace, a 900-file diff, a
        // deeper streak, a high score. No switch-case exists to be missing, so it still decides.
        {
            "a novel fact shape nobody enumerated",
            Failed(4) with { CheckDeclared = true, WorkspaceUnitCount = 47, ChangedFileCount = 900, RecordedReviewScore = 88 },
            QualityMechanism.SplitIntoSubtasks, "the check failed on 4 consecutive attempt(s) across 47 unit(s) and 900 changed file(s)"
        },
    };

    [Theory]
    [MemberData(nameof(RuleTable))]
    public void Decide_maps_each_ordered_row_and_cites_its_evidence(string row, QualityDecisionInput facts, QualityMechanism expected, string evidenceFragment)
    {
        var decision = QualityPolicy.Decide(facts);

        decision.Mechanism.ShouldBe(expected, $"row '{row}' must choose {expected}");
        decision.Reason.ShouldContain(evidenceFragment, Case.Sensitive, $"row '{row}' must cite the facts that matched, not a bare label");
    }

    /// <summary>
    /// ADVERSARIAL PRECEDENCE — one case per pair of rows that a single input can make BOTH match. Each input is
    /// built so the later row would also fire; the expectation is the earlier row's mechanism, so a reorder reddens
    /// here rather than shipping as a silent behaviour change. Pairs that cannot collide are absent on purpose (the
    /// disputed row now requires a PASS and the repeat rows require failures, so those two are mutually exclusive by
    /// construction rather than by ordering — the cases below pin that consequence instead).
    /// </summary>
    public static TheoryData<string, QualityDecisionInput, QualityMechanism> Orderings() => new()
    {
        {
            "affordability (1) beats the human-review row (2) — a mechanism nobody can pay for is not an option",
            Attempted(VerificationDisposition.HumanReviewRequired) with { BudgetCapUsd = 1m, SpendSoFarUsd = 1m, EstimatedNextAttemptCostUsd = 0.5m, CheckDeclared = true },
            QualityMechanism.Stop
        },
        {
            "the human-review row (2) beats the no-progress stop (3) — a human's verdict is still owed an answer",
            Attempted(VerificationDisposition.HumanReviewRequired) with { NoProgressDecisions = 8, MaxNoProgressDecisions = 8, CheckDeclared = true },
            QualityMechanism.AskHuman
        },
        {
            "the no-progress stop (3) beats the machinery-failed repair (4) — at the cap there is no increment left to repair with",
            Attempted(VerificationDisposition.InfraUnknown) with { NoProgressDecisions = 8, MaxNoProgressDecisions = 8, CheckDeclared = true },
            QualityMechanism.Stop
        },
        {
            "the machinery-failed repair (4) beats the no-check critic (7) — a broken check is not ungradable work",
            Attempted(VerificationDisposition.InfraUnknown) with { CheckDeclared = false },
            QualityMechanism.BoundedRepair
        },
        {
            "the unrun-check repair (6) beats the review-approved stop (12) — an approving review cannot stand in for a DECLARED check that never ran",
            Attempted(VerificationDisposition.Unknown) with { CheckDeclared = true, IndependentReviewRecorded = true, RecordedReviewScore = 95 },
            QualityMechanism.BoundedRepair
        },
        {
            "the no-check critic (7) beats the goal-met stop (9) — a passing verdict nobody declared a check for licenses no stop",
            Attempted(VerificationDisposition.Passed) with { CheckDeclared = false },
            QualityMechanism.IndependentCritic
        },
        {
            "the no-check critic (7) beats the repeat rows (10/11) — failures recorded without a declared check are not evidence about capability",
            Failed(2) with { CheckDeclared = false, ChangedFileCount = 3 },
            QualityMechanism.IndependentCritic
        },
        {
            "the disputed row (8) beats the goal-met stop (9) — a pass the producer's own claim contradicts still buys an independent look",
            Attempted(VerificationDisposition.Passed) with { CheckDeclared = true, SelfClaimContradictedTheCheck = true },
            QualityMechanism.IndependentCritic
        },
        {
            "a repeated failure escalates (11) rather than buying a critic — check and reviewer AGREE the work is bad, so a third opinion buys nothing",
            Failed(2) with { CheckDeclared = true, ChangedFileCount = 3, IndependentReviewRecorded = true, IndependentReviewDisapproved = true },
            QualityMechanism.EscalateModel
        },
        {
            "a repeated failure at scale splits (10) rather than buying a critic — same reason, and the work has seams",
            Failed(3) with { CheckDeclared = true, WorkspaceUnitCount = 4, IndependentReviewRecorded = true, IndependentReviewDisapproved = true },
            QualityMechanism.SplitIntoSubtasks
        },
        {
            "the repeat rows (10/11) beat the review-approved stop (12) — a run of failed verdicts outweighs an approving reviewer",
            Failed(2) with { CheckDeclared = false, ChangedFileCount = 3, IndependentReviewRecorded = true },
            QualityMechanism.EscalateModel
        },
    };

    [Theory]
    [MemberData(nameof(Orderings))]
    public void The_ordering_of_every_pair_of_rows_that_can_collide_is_pinned(string precedence, QualityDecisionInput facts, QualityMechanism expected)
    {
        QualityPolicy.Decide(facts).Mechanism.ShouldBe(expected, precedence);
    }

    [Fact]
    public void An_infra_classed_verdict_neither_accumulates_a_repeat_streak_nor_buys_a_stronger_model()
    {
        // The P22 invariant, now carried TWICE over: the machinery-failed row precedes every row that spends on the
        // work, AND the streak counts only work-classed Failed verdicts, so an infra tail cannot even reach the
        // repeat rows. Four infra failures on a wide, multi-unit diff would satisfy both repeat rows on scale alone.
        var facts = Repeated(4, VerificationDisposition.InfraUnknown) with { CheckDeclared = true, WorkspaceUnitCount = 6, ChangedFileCount = 120 };

        facts.ConsecutiveFailedVerdicts.ShouldBe(0, "an infra-classed verdict is not a failure of the work, so it starts no streak");
        QualityPolicy.Decide(facts).Mechanism.ShouldBe(QualityMechanism.BoundedRepair, "an infra-classed failure must never escalate the model or split the work");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_waived_verdict_stops_on_the_human_authorization_whether_or_not_a_check_was_declared(bool checkDeclared)
    {
        // A waiver is a human act, so it does not depend on whether machinery existed to waive. Before this row the
        // shape fell to the catch-all and re-attempted work a human had explicitly authorized stopping.
        var decision = QualityPolicy.Decide(Attempted(VerificationDisposition.Waived) with { CheckDeclared = checkDeclared });

        decision.Mechanism.ShouldBe(QualityMechanism.Stop);
        decision.Reason.ShouldContain("a human authorized forgoing verification", Case.Sensitive, "the stop must cite the authorization, and must not read as a pass");
        decision.Reason.Contains(nameof(VerificationDisposition.Passed), StringComparison.Ordinal).ShouldBeFalse("a waiver is NEVER a recorded pass (the amend-acceptance FATAL-1 invariant)");
    }

    [Theory]
    [InlineData(false, QualityMechanism.IndependentCritic)]   // nothing can grade it and nothing has reviewed it → buy the only evidence available
    [InlineData(true, QualityMechanism.Stop)]                 // …and once that review exists and approves, the work is done
    public void A_not_applicable_verdict_is_treated_as_no_declared_check(bool reviewRecorded, QualityMechanism expected)
    {
        // NotApplicable is the VACUOUS-grade classification: a check was declared but returned nothing to judge the
        // work by. Repairing it is pointless (nothing is broken), so it routes exactly as ungraded work does.
        var facts = Attempted(VerificationDisposition.NotApplicable) with { CheckDeclared = true, IndependentReviewRecorded = reviewRecorded, RecordedReviewScore = reviewRecorded ? 91 : null };

        QualityPolicy.Decide(facts).Mechanism.ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void An_undercounted_spend_is_disclosed_in_the_evidence_and_changes_no_mechanism(bool undercounted, bool expectDisclosure)
    {
        // The honesty qualifier annotates the reason; it never flips the decision. An undercount can only make the
        // affordability row fire LATER than it should, so padding the arithmetic would be inventing policy.
        var facts = new QualityDecisionInput { BudgetCapUsd = 1m, SpendSoFarUsd = 0.9m, EstimatedNextAttemptCostUsd = 0.5m, CheckDeclared = true, SpendIsUndercounted = undercounted };

        var decision = QualityPolicy.Decide(facts);

        decision.Mechanism.ShouldBe(QualityMechanism.Stop, "the qualifier annotates the evidence, it does not choose a mechanism");
        decision.Reason.Contains("known undercount", StringComparison.Ordinal).ShouldBe(expectDisclosure);
    }

    [Theory]
    [InlineData(null, QualityMechanism.Stop)]   // absence of a score is NOT doubt — the lifted comparison leaves it unmatched
    [InlineData(69, QualityMechanism.IndependentCritic)]
    [InlineData(70, QualityMechanism.Stop)]     // at the floor is confident enough
    [InlineData(100, QualityMechanism.Stop)]
    public void A_recorded_score_is_uncertainty_only_below_the_floor_and_a_null_score_is_absence(int? score, QualityMechanism expected)
    {
        var facts = Attempted(VerificationDisposition.Passed) with { CheckDeclared = true, RecordedReviewScore = score };

        QualityPolicy.Decide(facts).Mechanism.ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, QualityMechanism.SingleAgent)]        // one failure is a failure, not a repetition
    [InlineData(2, QualityMechanism.EscalateModel)]      // the second failure is the first repeat
    [InlineData(5, QualityMechanism.EscalateModel)]
    public void The_repeat_floor_is_where_a_failing_check_becomes_evidence(int streak, QualityMechanism expected)
    {
        var facts = Failed(streak) with { CheckDeclared = true, ChangedFileCount = 2 };

        QualityPolicy.Decide(facts).Mechanism.ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, QualityMechanism.EscalateModel)]        // one unit has no seam the plan did not have to invent
    [InlineData(2, QualityMechanism.SplitIntoSubtasks)]    // a second independently-checkoutable unit IS that seam
    public void The_unit_scale_floor_is_where_a_repeated_failure_prefers_splitting_over_escalating(int units, QualityMechanism expected)
    {
        // The BEHAVIOURAL boundary, not just the literal: pinning the constant alone lets an off-by-one move both
        // the constant and the one row that reads it, and stay green.
        var facts = Failed(2) with { CheckDeclared = true, WorkspaceUnitCount = units, ChangedFileCount = 3 };

        QualityPolicy.Decide(facts).Mechanism.ShouldBe(expected);
    }

    [Fact]
    public void A_streak_counts_only_the_failed_verdicts_at_the_tail()
    {
        // A pass between two failures breaks the run: the evidence is "the check keeps failing NOW", not "it failed
        // twice at some point". The weaker claim is deliberate — nothing records WHICH failure each attempt was, so
        // "the same failure repeated" is not decidable and the table never asserts it.
        var interrupted = new QualityDecisionInput
        {
            CheckDeclared = true,
            ChangedFileCount = 2,
            Attempts = new[]
            {
                new QualityAttemptFact { Disposition = VerificationDisposition.Failed },
                new QualityAttemptFact { Disposition = VerificationDisposition.Passed },
                new QualityAttemptFact { Disposition = VerificationDisposition.Failed },
            },
        };

        interrupted.ConsecutiveFailedVerdicts.ShouldBe(1);
        QualityPolicy.Decide(interrupted).Mechanism.ShouldBe(QualityMechanism.SingleAgent);
    }

    [Theory]
    [InlineData(null, 0.5, QualityMechanism.SingleAgent)]   // uncapped work is never stopped for budget
    [InlineData(1.0, null, QualityMechanism.SingleAgent)]   // an unrecorded next-attempt cost cannot prove unaffordability
    [InlineData(1.0, 0.5, QualityMechanism.Stop)]           // both recorded, and the remainder is short
    public void The_affordability_row_fires_only_on_recorded_evidence(double? cap, double? nextCost, QualityMechanism expected)
    {
        var facts = new QualityDecisionInput
        {
            CheckDeclared = true,
            SpendSoFarUsd = 0.8m,
            BudgetCapUsd = cap is null ? null : (decimal)cap,
            EstimatedNextAttemptCostUsd = nextCost is null ? null : (decimal)nextCost,
        };

        QualityPolicy.Decide(facts).Mechanism.ShouldBe(expected);
    }

    [Fact]
    public void The_remainder_never_reads_as_a_negative_allowance()
    {
        new QualityDecisionInput { BudgetCapUsd = 1m, SpendSoFarUsd = 2.5m }.RemainingUsd.ShouldBe(0m);
        new QualityDecisionInput { SpendSoFarUsd = 2.5m }.RemainingUsd.ShouldBeNull("uncapped work has no remainder to report");
    }

    [Fact]
    public void Decide_is_total_over_every_recorded_fact_combination()
    {
        var defined = Enum.GetValues<QualityMechanism>();
        var evaluated = 0;

        foreach (var disposition in Enum.GetValues<VerificationDisposition>())
        foreach (var declared in Bools)
        foreach (var attempts in new[] { 0, 1, 2 })
        foreach (var disapproved in Bools)
        foreach (var reviewRecorded in Bools)
        {
            var facts = new QualityDecisionInput
            {
                CheckDeclared = declared,
                IndependentReviewRecorded = reviewRecorded,
                IndependentReviewDisapproved = disapproved,
                Attempts = Enumerable.Range(0, attempts).Select(_ => new QualityAttemptFact { Disposition = disposition }).ToArray(),
            };

            var decision = QualityPolicy.Decide(facts);

            decision.Mechanism.ShouldBeOneOf(defined);
            decision.Reason.ShouldNotBeNullOrWhiteSpace("every row authors evidence — a stop or a spend with no reason is the thing P22 forbids");
            evaluated++;
        }

        evaluated.ShouldBe(7 * 2 * 3 * 2 * 2);
    }

    [Fact]
    public void Every_mechanism_in_the_closed_set_is_reachable_from_some_recorded_evidence()
    {
        // A mechanism no evidence can reach is a dead option that 9c's ablation could never measure. The rule table
        // and the enum must stay in step: adding a member without a row fails here.
        var reached = RuleTable().Select(row => (QualityMechanism)row[2]!).ToHashSet();

        reached.ShouldBe(Enum.GetValues<QualityMechanism>().ToHashSet(), ignoreOrder: true);
    }

    [Fact]
    public void The_policy_thresholds_are_pinned()
    {
        // These four numbers are the boundaries P22-9c's same-budget ablation moves. Pinned so a change is a
        // deliberate, reviewed edit rather than a silent recalibration of every run's quality spend.
        QualityPolicy.ConfidentReviewScoreFloor.ShouldBe(70);
        QualityPolicy.RepeatedFailureFloor.ShouldBe(2);
        QualityPolicy.SplitScaleFileFloor.ShouldBe(10);
        QualityPolicy.SplitScaleUnitFloor.ShouldBe(2);
    }

    /// <summary>One attempt with the given recorded verdict.</summary>
    private static QualityDecisionInput Attempted(VerificationDisposition disposition) => Repeated(1, disposition);

    /// <summary><paramref name="count"/> attempts whose check failed.</summary>
    private static QualityDecisionInput Failed(int count) => Repeated(count, VerificationDisposition.Failed);

    /// <summary><paramref name="count"/> attempts that all recorded the same verdict.</summary>
    private static QualityDecisionInput Repeated(int count, VerificationDisposition disposition) =>
        new() { Attempts = Enumerable.Range(0, count).Select(_ => new QualityAttemptFact { Disposition = disposition }).ToArray() };

    private static readonly bool[] Bools = { false, true };
}
