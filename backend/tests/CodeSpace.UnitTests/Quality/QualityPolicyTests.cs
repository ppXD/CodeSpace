using CodeSpace.Core.Services.Quality;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Failures;
using CodeSpace.Messages.Quality;
using Shouldly;

namespace CodeSpace.UnitTests.Quality;

/// <summary>
/// Pins the PURE adaptive quality policy (P22-9a) — the ordered rule table that chooses a mechanism from RECORDED
/// evidence. Every ordered row gets its own theory case (so a reorder or a predicate change is a test-visible
/// decision), the three soundness orderings are pinned as their own facts, a NOVEL fact shape nobody enumerated is
/// pinned to prove no switch-case is consulted, and a bounded cartesian sweep proves the table is TOTAL and that
/// only a stop ever reports a known marginal value.
/// </summary>
[Trait("Category", "Unit")]
public sealed class QualityPolicyTests
{
    /// <summary>
    /// One case per ORDERED ROW, in table order, each asserting both the mechanism and the fact-citing evidence.
    /// The cases are built as whole fact records rather than a 13-column InlineData: naming only the facts that
    /// matter is what makes each row's trigger readable.
    /// </summary>
    public static TheoryData<string, QualityDecisionInput, QualityMechanism, string> RuleTable() => new()
    {
        // Row 1 — the recorded remainder cannot buy another attempt. FIRST, because a mechanism nobody can pay for is not an option.
        {
            "budget below the cost of another attempt",
            new QualityDecisionInput { BudgetCapUsd = 1.00m, SpendSoFarUsd = 0.95m, EstimatedNextAttemptCostUsd = 0.20m, CheckDeclared = true },
            QualityMechanism.Stop, "below the 0.20 USD another attempt costs"
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
        // Row 4 — infra-classed by VERDICT, and infra-classed by FAILURE KIND. Never a stronger model.
        {
            "an infra-classed verdict",
            Attempted(VerificationDisposition.InfraUnknown) with { CheckDeclared = true },
            QualityMechanism.BoundedRepair, "failed the machinery, not the work"
        },
        {
            "a dependency-classed failure kind",
            Attempted(VerificationDisposition.Failed, FailureKind.Unavailable) with { CheckDeclared = true },
            QualityMechanism.BoundedRepair, "a stronger model cannot fix a check that could not run"
        },
        // Row 5 — a check was declared, work was attempted, and no verdict came back.
        {
            "a declared check that never ran",
            Attempted(VerificationDisposition.Unknown) with { CheckDeclared = true },
            QualityMechanism.BoundedRepair, "a check is declared but 1 attempt(s) recorded no verdict"
        },
        // Row 6 — work exists that nothing objective can grade.
        {
            "no check declared for work already produced",
            Attempted(VerificationDisposition.Unknown) with { CheckDeclared = false },
            QualityMechanism.IndependentCritic, "no objective check is declared"
        },
        // Row 7 — the recorded evidence disagrees with itself, by each of its three recorded carriers.
        {
            "the self-claim contradicted the check",
            Attempted(VerificationDisposition.Passed) with { CheckDeclared = true, SelfClaimContradictedTheCheck = true },
            QualityMechanism.IndependentCritic, "self-claim contradicted the check: True"
        },
        {
            "an independent reviewer disapproved",
            Attempted(VerificationDisposition.Passed) with { CheckDeclared = true, IndependentReviewDisapproved = true },
            QualityMechanism.IndependentCritic, "independent review disapproved: True"
        },
        {
            "a recorded score below the confident floor",
            Attempted(VerificationDisposition.Passed) with { CheckDeclared = true, RecordedReviewScore = 40 },
            QualityMechanism.IndependentCritic, "recorded score: 40 against a floor of 70"
        },
        // Row 8 — the declared check passed and nothing recorded disputes it.
        {
            "the declared check passed undisputed",
            Attempted(VerificationDisposition.Passed) with { CheckDeclared = true },
            QualityMechanism.Stop, "nothing recorded disputes it"
        },
        // Row 9 — the same work failure repeats on work that already has seams: split, don't escalate.
        {
            "a repeated work failure across several units",
            Repeated(2, FailureKind.Unprocessable) with { CheckDeclared = true, WorkspaceUnitCount = 3, ChangedFileCount = 2 },
            QualityMechanism.SplitIntoSubtasks, "already has seams to split along"
        },
        {
            "a repeated work failure across a wide diff",
            Repeated(2, FailureKind.Unprocessable) with { CheckDeclared = true, WorkspaceUnitCount = 1, ChangedFileCount = 10 },
            QualityMechanism.SplitIntoSubtasks, "across 1 unit(s) and 10 changed file(s)"
        },
        // Row 10 — the same work failure repeats on work too localized to split: the capability changes instead.
        {
            "a repeated work failure on a localized diff",
            Repeated(2, FailureKind.Invalid) with { CheckDeclared = true, WorkspaceUnitCount = 1, ChangedFileCount = 3 },
            QualityMechanism.EscalateModel, "too localized to split, so the capability is what changes"
        },
        // Row 11 (catch-all) — a single failure is not yet a repeat, and a fresh unit has nothing to review.
        {
            "one work failure, not yet a repeat",
            Attempted(VerificationDisposition.Failed, FailureKind.Invalid) with { CheckDeclared = true },
            QualityMechanism.SingleAgent, "nothing recorded rules out another ordinary attempt"
        },
        {
            "a fresh unit with nothing recorded at all",
            new QualityDecisionInput(),
            QualityMechanism.SingleAgent, "the cheapest mechanism is the baseline"
        },
        // The REFUTATION row: a fact shape no case above enumerates — a 47-unit workspace, a 900-file diff, a
        // failure kind used nowhere else, a deeper streak. No switch-case exists to be missing, so it still decides.
        {
            "a novel fact shape nobody enumerated",
            Repeated(4, FailureKind.Forbidden) with { CheckDeclared = true, WorkspaceUnitCount = 47, ChangedFileCount = 900, RecordedReviewScore = 88 },
            QualityMechanism.SplitIntoSubtasks, "4 consecutive attempt(s) failed the work the same way (Forbidden) across 47 unit(s) and 900 changed file(s)"
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

    [Fact]
    public void An_infra_classed_failure_never_buys_a_stronger_model_however_often_it_repeats()
    {
        // The P22 invariant, and the reason the infra row precedes the escalate/split rows: a check that could not
        // run is not evidence that the work needs more capability. Four identical infra failures on a wide,
        // multi-unit diff would satisfy BOTH repeat rows on scale alone.
        var facts = Repeated(4, FailureKind.Unavailable) with { CheckDeclared = true, WorkspaceUnitCount = 6, ChangedFileCount = 120 };

        var decision = QualityPolicy.Decide(facts);

        decision.Mechanism.ShouldBe(QualityMechanism.BoundedRepair, "an infra-classed failure must never escalate the model or split the work");
        decision.Reason.ShouldContain("a stronger model cannot fix a check that could not run", Case.Sensitive);
    }

    [Fact]
    public void A_passing_check_that_recorded_evidence_disputes_still_buys_an_independent_look()
    {
        // Pins row 7 BEFORE row 8: a Passed verdict the producer's own self-claim contradicts must not ship on the
        // strength of the verdict alone. A reorder would silently turn this into a Stop, so it is pinned here.
        var facts = Attempted(VerificationDisposition.Passed) with { CheckDeclared = true, SelfClaimContradictedTheCheck = true };

        QualityPolicy.Decide(facts).Mechanism.ShouldBe(QualityMechanism.IndependentCritic);
    }

    [Fact]
    public void An_unaffordable_run_stops_on_the_budget_evidence_even_when_another_row_would_also_match()
    {
        // Pins row 1 FIRST: this input would otherwise match the human-review row. A mechanism nobody can pay for
        // is not an option, so the affordability gate has to precede every row that spends.
        var facts = Attempted(VerificationDisposition.HumanReviewRequired) with { BudgetCapUsd = 1m, SpendSoFarUsd = 1m, EstimatedNextAttemptCostUsd = 0.5m, CheckDeclared = true };

        var decision = QualityPolicy.Decide(facts);

        decision.Mechanism.ShouldBe(QualityMechanism.Stop);
        decision.Reason.ShouldContain("leaves 0 USD", Case.Sensitive, "the stop must cite the budget arithmetic, not merely announce a stop");
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
    [InlineData(2, QualityMechanism.EscalateModel)]      // the second identical failure is the first repeat
    [InlineData(5, QualityMechanism.EscalateModel)]
    public void The_repeat_floor_is_where_an_identical_work_failure_becomes_evidence(int streak, QualityMechanism expected)
    {
        var facts = Repeated(streak, FailureKind.Invalid) with { CheckDeclared = true, ChangedFileCount = 2 };

        QualityPolicy.Decide(facts).Mechanism.ShouldBe(expected);
    }

    [Fact]
    public void A_streak_only_counts_attempts_that_failed_the_work_the_SAME_way()
    {
        // Two failures of DIFFERENT kinds are not "the same thing happening again", so they buy no mechanism beyond
        // another ordinary attempt — the repeat rules key on the classification pair, never on the raw count.
        var mixed = new QualityDecisionInput
        {
            CheckDeclared = true,
            Attempts = new[]
            {
                new QualityAttemptFact { Disposition = VerificationDisposition.Failed, Failure = FailureKind.Invalid },
                new QualityAttemptFact { Disposition = VerificationDisposition.Failed, Failure = FailureKind.Conflict },
            },
        };

        mixed.IdenticalWorkFailureStreak.ShouldBe(1);
        QualityPolicy.Decide(mixed).Mechanism.ShouldBe(QualityMechanism.SingleAgent);
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
        var failures = new FailureKind?[] { null, FailureKind.Invalid, FailureKind.Unavailable, FailureKind.Exhausted, FailureKind.Internal };
        var evaluated = 0;

        foreach (var disposition in Enum.GetValues<VerificationDisposition>())
        foreach (var failure in failures)
        foreach (var declared in Bools)
        foreach (var attempts in new[] { 0, 1, 2 })
        foreach (var disputed in Bools)
        {
            var facts = new QualityDecisionInput
            {
                CheckDeclared = declared,
                IndependentReviewDisapproved = disputed,
                Attempts = Enumerable.Range(0, attempts).Select(_ => new QualityAttemptFact { Disposition = disposition, Failure = failure }).ToArray(),
            };

            var decision = QualityPolicy.Decide(facts);

            decision.Mechanism.ShouldBeOneOf(defined);
            decision.Reason.ShouldNotBeNullOrWhiteSpace("every row authors evidence — a stop or a spend with no reason is the thing P22 forbids");
            evaluated++;

            // Only a stop knows its marginal value (zero — no further spend can change the outcome). Every other
            // mechanism reports null: unknown until 9c's ablation measures it, never an invented estimate.
            if (decision.Mechanism == QualityMechanism.Stop)
                decision.ExpectedMarginalValue.ShouldBe(0d);
            else
                decision.ExpectedMarginalValue.ShouldBeNull();
        }

        evaluated.ShouldBe(7 * 5 * 2 * 3 * 2);
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
        // These three numbers are the boundaries P22-9c's same-budget ablation moves. Pinned so a change is a
        // deliberate, reviewed edit rather than a silent recalibration of every run's quality spend.
        QualityPolicy.ConfidentReviewScoreFloor.ShouldBe(70);
        QualityPolicy.RepeatedFailureFloor.ShouldBe(2);
        QualityPolicy.SplitScaleFileFloor.ShouldBe(10);
    }

    /// <summary>One attempt with the given recorded classifications.</summary>
    private static QualityDecisionInput Attempted(VerificationDisposition disposition, FailureKind? failure = null) =>
        new() { Attempts = new[] { new QualityAttemptFact { Disposition = disposition, Failure = failure } } };

    /// <summary><paramref name="count"/> attempts that all failed the work the same way.</summary>
    private static QualityDecisionInput Repeated(int count, FailureKind failure) =>
        new() { Attempts = Enumerable.Range(0, count).Select(_ => new QualityAttemptFact { Disposition = VerificationDisposition.Failed, Failure = failure }).ToArray() };

    private static readonly bool[] Bools = { false, true };
}
