using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Quality;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Quality;
using Shouldly;

namespace CodeSpace.UnitTests.Quality;

/// <summary>
/// P22-9b — the supervisor→quality ADAPTER. Every test here pins a GRAIN decision P22-9a deliberately refused to
/// make, because each of them is a silent mis-stop when it is read at the wrong grain and none of them is visible in
/// the policy's own tests: the policy is pure over a record whose members do not say where they came from.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SupervisorQualityFactsTests
{
    [Fact]
    public void Attempts_arrive_oldest_first_so_a_failure_streak_counts_back_from_the_newest()
    {
        // The order is load-bearing and REVERSIBLE-LOOKING: a reversed walk still yields two attempts and the same
        // multiset of verdicts, so nothing but this assertion separates "the check keeps failing" from "it failed
        // twice and then passed". Read newest-first, a fixed unit reads as a live two-failure streak.
        var context = Context(Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Failed()),
            Retry(3, "s1", Failed()),
            Retry(4, "s1", Passed()));

        var facts = Facts("s1", context);

        facts.Attempts.Select(a => a.Disposition).ShouldBe(new[] { VerificationDisposition.Failed, VerificationDisposition.Failed, VerificationDisposition.Passed });
        facts.ConsecutiveFailedVerdicts.ShouldBe(0, "the newest attempt PASSED — the two older failures are history, not a streak");
        facts.LatestDisposition.ShouldBe(VerificationDisposition.Passed);
    }

    [Fact]
    public void Only_this_units_own_attempts_are_read()
    {
        // The positional subtaskIds[i] ↔ agentResults[i] join: a sibling's failures must never enter this unit's
        // streak, or one bad unit in a fan-out escalates every unit beside it.
        var context = Context(Plan(1, ("s1", "First"), ("s2", "Second")),
            Spawn(2, new[] { "s1", "s2" }, Passed(), Failed()));

        Facts("s1", context).Attempts.Single().Disposition.ShouldBe(VerificationDisposition.Passed);
        Facts("s2", context).Attempts.Single().Disposition.ShouldBe(VerificationDisposition.Failed);
    }

    [Fact]
    public void The_budget_is_read_at_the_PLAN_grain_on_both_sides_of_the_subtraction()
    {
        // The pinned choice: the run's cap paired with the RUN's spend. Substituting a per-unit spend (this unit's
        // own attempt costs) under the same run cap is the mismatch 9a warned about — the numbers below are chosen
        // so the two readings differ, so a task-grain substitution reddens here rather than in production.
        var context = Context(Plan(1, ("s1", "First")), Spawn(2, new[] { "s1" }, Failed())) with
        {
            MaxCostUsd = 9m,
            RunSpendUsd = 7m,
        };

        var facts = Facts("s1", context);

        facts.BudgetCapUsd.ShouldBe(9m);
        facts.SpendSoFarUsd.ShouldBe(7m, "the RUN's realized spend — not the unit's own attempt costs, which are 0 here");
        facts.RemainingUsd.ShouldBe(2m);
    }

    [Fact]
    public void An_unpriced_spend_is_disclosed_as_an_undercount()
    {
        var context = Context(Plan(1, ("s1", "First")), Spawn(2, new[] { "s1" }, Failed())) with { UnpricedSpendModel = "some-unpriceable-model" };

        Facts("s1", context).SpendIsUndercounted.ShouldBeTrue();
    }

    [Fact]
    public void The_next_attempt_estimate_is_the_MAX_of_the_units_priced_attempts_never_the_mean()
    {
        // [cheap, expensive] is the discriminating corpus: max = 6, mean = 3.5, last = 6, first = 1. Only the mean
        // is plausible enough to be written by mistake, and it under-estimates the next attempt — which is the
        // direction that spends real money past the cap.
        var context = Context(Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Priced(inputTokens: 1_000_000, outputTokens: 0)),
            Retry(3, "s1", Priced(inputTokens: 6_000_000, outputTokens: 0))) with { ModelPrices = OneDollarPerMillionInputTokens };

        Facts("s1", context).EstimatedNextAttemptCostUsd.ShouldBe(6m, "the MAX over the unit's priced attempts — a mean (3.5) under-estimates and under-stops");
    }

    [Fact]
    public void An_unpriced_unit_estimates_nothing_rather_than_guessing()
    {
        // Every scenario in this suite that does NOT set ModelPrices exercises the same absence, but pin it
        // explicitly: a 0 here would read as "another attempt is free" and the affordability row could never fire.
        var context = Context(Plan(1, ("s1", "First")), Spawn(2, new[] { "s1" }, Failed())) with { MaxCostUsd = 1m, RunSpendUsd = 1m };

        var facts = Facts("s1", context);

        facts.EstimatedNextAttemptCostUsd.ShouldBeNull("no attempt reported priced usage, so nothing recorded says what another attempt costs");
        QualityPolicy.Decide(facts).Mechanism.ShouldNotBe(QualityMechanism.Stop, "an exhausted cap with no recorded rate must not stop the run on a guess");
    }

    [Fact]
    public void An_over_claim_is_carried_as_the_self_claim_contradiction()
    {
        var context = Context(Plan(1, ("s1", "First")), Spawn(2, new[] { "s1" }, OverClaimed()));

        Facts("s1", context).SelfClaimContradictedTheCheck.ShouldBeTrue();
    }

    [Fact]
    public void An_outstanding_amendment_nulls_the_contradiction_the_way_the_retry_escalation_does()
    {
        // B5's ruling, reproduced here rather than re-argued: a contradiction graded by an oracle a human has since
        // amended is stale evidence. The retry escalation already drops it; a quality reading that kept it would
        // have the two mechanisms disagree about a fact, not about a threshold.
        var context = Context(Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, OverClaimed()),
            ApprovedAmendment(3, "s1"));

        Facts("s1", context).SelfClaimContradictedTheCheck
            .ShouldBeFalse("the self-report never disagreed with the CO-SIGNED check, only with the dead one");
    }

    [Fact]
    public void A_waived_attempt_carries_the_human_waiver_and_never_a_pass()
    {
        // The amend-acceptance FATAL-1 invariant at the fact layer: Waived must survive as Waived. Falling back to
        // Classify here (the unit's AcceptancePassed is null) would read it as Unknown and re-attempt work a human
        // authorized forgoing — and the policy's Waived row, which cites the authorization, would never fire.
        var context = Context(Plan(1, ("s1", "First")), Spawn(2, new[] { "s1" }, Waived()));

        var facts = Facts("s1", context);

        facts.LatestDisposition.ShouldBe(VerificationDisposition.Waived);

        var decision = QualityPolicy.Decide(facts);

        decision.Mechanism.ShouldBe(QualityMechanism.Stop);
        decision.Reason.ShouldContain("a human authorized forgoing verification");
    }

    [Fact]
    public void The_review_facts_are_absent_because_no_per_unit_work_review_is_recorded()
    {
        // The mutation this catches: mapping a SupervisorDecisionReview (the decider's DRAFT critic, at decision
        // scope) onto these members. That would fabricate a verdict about work no reviewer ever read, and it is
        // then free to dispute a PASSING acceptance check or to settle ungraded work forever.
        var context = Context(Plan(1, ("s1", "First")), Spawn(2, new[] { "s1" }, Passed())) with { DecisionReviewMode = Messages.Enums.ReviewMode.Improve, ReviewerCritique = "the plan looks wrong to me" };

        var facts = Facts("s1", context);

        facts.IndependentReviewRecorded.ShouldBeFalse();
        facts.IndependentReviewDisapproved.ShouldBeFalse();
        facts.RecordedReviewScore.ShouldBeNull();
    }

    // ─── the UNIT grain of the declared-check fact ─────────────────────────────

    /// <summary>
    /// The grain ruling: a check is declared for a unit when the operator's run-wide floor is declared OR the unit's
    /// OWN effective oracle exists. The FIRST row is the one the floor-only reading reddens — and it is the common
    /// shape, since most runs declare no floor at all.
    /// </summary>
    [Theory]
    [InlineData(false, true, true)]     // no operator floor, the unit authored its own oracle → declared
    [InlineData(true, false, true)]     // the operator floor alone, no unit oracle → declared
    [InlineData(false, false, false)]   // neither — nothing that could grade this unit is declared
    public void A_check_is_declared_when_EITHER_the_operator_floor_or_this_units_own_oracle_exists(bool floorDeclared, bool unitOracleAuthored, bool expected)
    {
        var plan = unitOracleAuthored ? PlanWithUnitOracles(1, ("s1", "First")) : Plan(1, ("s1", "First"));
        var context = Context(plan, Spawn(2, new[] { "s1" }, Passed())) with { AcceptanceChecks = floorDeclared ? new[] { "dotnet", "test" } : null };

        Facts("s1", context).CheckDeclared.ShouldBe(expected);
    }

    [Fact]
    public void A_unit_graded_by_its_OWN_oracle_reads_as_graded_rather_than_as_work_nothing_can_grade()
    {
        // The consequence the grain ruling was made for, on the common no-floor run: the same prompt recites this
        // unit's PASSED verdict one block above. Read at the operator-floor grain the recommendation was
        // IndependentCritic — "no objective check can grade this work" about a unit its own oracle had just graded,
        // which is one prompt disagreeing with itself, and the reason it cannot recur is asserted here.
        var context = Context(PlanWithUnitOracles(1, ("s1", "First")), Spawn(2, new[] { "s1" }, Passed()));

        var decision = QualityPolicy.Decide(Facts("s1", context));

        decision.Mechanism.ShouldBe(QualityMechanism.Stop);
        decision.Reason.ShouldContain("recorded a Passed verdict from a declared check");
    }

    [Fact]
    public void The_unit_oracle_is_the_EFFECTIVE_one_so_a_co_signed_amendment_declares_a_check_the_plan_never_authored()
    {
        // Why this reads through SupervisorAcceptanceOverlay rather than off the planned subtask: an approved
        // amendment can ADD the oracle a plan left unauthored, and the fold that grades the unit honours it. A
        // re-derivation from `subtask.Acceptance` passes every row of the theory above and reds only here.
        var context = Context(Plan(1, ("s1", "First")), Spawn(2, new[] { "s1" }, Passed()), ApprovedAmendment(3, "s1"));

        Facts("s1", context).CheckDeclared.ShouldBeTrue("the co-signed amendment IS this unit's effective check, and it is what the unit is graded against");
    }

    [Fact]
    public void Scale_is_read_off_the_latest_attempt_and_prefers_the_pre_clip_total()
    {
        var context = Context(Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Failed()),
            Retry(3, "s1", FailedWithScale(changedFiles: new[] { "a.cs", "b.cs" }, totalChangedFiles: 41, repositoryAliases: new[] { "app", "lib" })));

        var facts = Facts("s1", context);

        facts.ChangedFileCount.ShouldBe(41, "the clipped list is a projection artefact — the recorded total is the scale");
        facts.WorkspaceUnitCount.ShouldBe(2);
    }

    // ─── the fold ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_fold_covers_every_attempted_unit_and_skips_the_pending_ones()
    {
        var context = Context(Plan(1, ("s1", "First"), ("s2", "Second"), ("s3", "Third")),
            Spawn(2, new[] { "s1", "s2" }, Passed(), Failed()));

        SupervisorQualityFacts.DecideAll(context).Select(d => d.SubtaskId).ShouldBe(new[] { "s1", "s2" },
            "s3 was never attempted — a baseline recommendation over no evidence is not a reading worth reciting");
    }

    [Fact]
    public void The_fold_reads_the_NEWEST_plans_units()
    {
        var context = Context(Plan(1, ("old", "Superseded")),
            Spawn(2, new[] { "old" }, Failed()),
            Plan(3, ("s1", "First")),
            Spawn(4, new[] { "s1" }, Failed()));

        SupervisorQualityFacts.DecideAll(context).Select(d => d.SubtaskId).ShouldBe(new[] { "s1" });
    }

    [Fact]
    public void A_reading_carries_the_facts_it_was_computed_from()
    {
        var context = Context(Plan(1, ("s1", "First")), Spawn(2, new[] { "s1" }, Failed()));

        var reading = SupervisorQualityFacts.DecideAll(context).Single();

        reading.Facts.AttemptCount.ShouldBe(1);
        reading.Reason.ShouldBe(QualityPolicy.Decide(reading.Facts).Reason, "the reading recites the policy's own reason verbatim, never a re-worded copy");
    }

    // ─── the named divergence with the legacy escalation trigger (#1954) ───────

    [Fact]
    public void The_legacy_trigger_escalates_a_first_over_claim_the_policy_would_spend_one_more_ordinary_attempt_on()
    {
        // The divergence #1954 named, asserted end to end over ONE tape rather than argued: the retry escalation
        // fires on the first contradiction; the policy's repeat floor is two consecutive failed verdicts, and its
        // disputed row is guarded on a PASS, so an over-claim (whose check failed) reaches the baseline instead.
        // 9b keeps BOTH and records the disagreement — this is the case that makes PolicyAgrees false.
        var context = Context(Plan(1, ("s1", "First")), Spawn(2, new[] { "s1" }, OverClaimed())) with { AcceptanceChecks = new[] { "dotnet test" } };

        SupervisorRetryEscalation.EscalationReason(AgentContradiction.OverClaim, noProgressDecisions: 0, maxNoProgressDecisions: 8)
            .ShouldNotBeNull("the legacy trigger escalates on a single contradiction");

        var recommended = QualityPolicy.Decide(Facts("s1", context)).Mechanism;

        recommended.ShouldBe(QualityMechanism.SingleAgent, "one failure is not yet evidence about capability");
        SupervisorRetryEscalation.PolicyAgrees(recommended).ShouldBe(false);
    }

    [Fact]
    public void A_second_consecutive_failure_on_a_localized_diff_is_where_the_two_agree()
    {
        var context = Context(Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, FailedWithScale(new[] { "a.cs" }, totalChangedFiles: null, repositoryAliases: Array.Empty<string>())),
            Retry(3, "s1", FailedWithScale(new[] { "a.cs" }, totalChangedFiles: null, repositoryAliases: Array.Empty<string>()))) with { AcceptanceChecks = new[] { "dotnet test" } };

        var recommended = QualityPolicy.Decide(Facts("s1", context)).Mechanism;

        recommended.ShouldBe(QualityMechanism.EscalateModel);
        SupervisorRetryEscalation.PolicyAgrees(recommended).ShouldBe(true);
    }

    [Fact]
    public void No_reading_for_a_unit_is_an_absence_of_opinion_never_a_disagreement() =>
        SupervisorRetryEscalation.PolicyAgrees(null).ShouldBeNull();

    // ─── tape builders ─────────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, ModelPrice> OneDollarPerMillionInputTokens =
        new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase) { ["fixture-model"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 1m } };

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] priors) => new() { PriorDecisions = priors };

    /// <summary>One unit's facts against the run's EFFECTIVE oracle view, resolved by the production fold's own helper — never by a second copy of those two lines here, which would be a mirror with nothing detecting its drift (Rule 12.5).</summary>
    private static QualityDecisionInput Facts(string subtaskId, SupervisorTurnContext context) =>
        SupervisorQualityFacts.For(subtaskId, context, SupervisorQualityFacts.EffectiveAcceptanceFor(context).BySubtask);

    private static SupervisorPriorDecision Plan(int seq, params (string Id, string Title)[] subtasks) =>
        Prior(seq, SupervisorDecisionKinds.Plan,
            JsonSerializer.Serialize(new { subtasks = subtasks.Select(s => new { id = s.Id, title = s.Title, instruction = $"do {s.Id}" }).ToArray() }, AgentJson.Options));

    /// <summary>A plan whose every unit AUTHORS its own acceptance oracle — the shape a per-unit verdict can only have been folded from, and the one the operator-floor reading could not see.</summary>
    private static SupervisorPriorDecision PlanWithUnitOracles(int seq, params (string Id, string Title)[] subtasks) =>
        Prior(seq, SupervisorDecisionKinds.Plan,
            JsonSerializer.Serialize(new { subtasks = subtasks.Select(s => new { id = s.Id, title = s.Title, instruction = $"do {s.Id}", acceptance = new { command = new[] { "dotnet", "test" } } }).ToArray() }, AgentJson.Options));

    private static SupervisorPriorDecision Spawn(int seq, string[] subtaskIds, params object[] results) =>
        Prior(seq, SupervisorDecisionKinds.Spawn,
            JsonSerializer.Serialize(new { subtaskIds }, AgentJson.Options),
            JsonSerializer.Serialize(new { agentResults = results }, AgentJson.Options));

    private static SupervisorPriorDecision Retry(int seq, string subtaskId, object result) =>
        Prior(seq, SupervisorDecisionKinds.Retry,
            JsonSerializer.Serialize(new { subtaskId }, AgentJson.Options),
            JsonSerializer.Serialize(new { agentResults = new[] { result } }, AgentJson.Options));

    /// <summary>
    /// An APPROVED oracle amendment for one unit — minted through the PRODUCTION card builder
    /// (<c>SupervisorAmendAcceptance.IntoAskHuman</c>) rather than a hand-written payload, because the
    /// outstanding-amendment read is gated on the card's own question marker: a fixture that wrote its own bytes
    /// would silently read as "no amendment" and the test would pass for the wrong reason.
    /// </summary>
    private static SupervisorPriorDecision ApprovedAmendment(int seq, string subtaskId)
    {
        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = subtaskId,
            Reason = "the old check could not run",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "dotnet", "test" } },
        });

        return Prior(seq, SupervisorDecisionKinds.AskHuman, card.PayloadJson, JsonSerializer.Serialize(new { question = "q", answer = "approve" }, AgentJson.Options));
    }

    private static object Passed() => new { agentRunId = Guid.NewGuid(), status = "Succeeded", acceptancePassed = true, acceptanceDetail = "tests-passed", changedFiles = new[] { "a.cs" } };

    private static object Failed() => new { agentRunId = Guid.NewGuid(), status = "Failed", acceptancePassed = false, acceptanceDetail = "tests-failed-exit-1", changedFiles = new[] { "a.cs" } };

    /// <summary>The over-claim shape: the agent reported success, its own check FAILED, and the fold recorded the contradiction.</summary>
    private static object OverClaimed() =>
        new { agentRunId = Guid.NewGuid(), status = "Succeeded", acceptancePassed = false, acceptanceDetail = "tests-failed-exit-1", contradiction = AgentContradiction.OverClaim, changedFiles = new[] { "a.cs" } };

    /// <summary>A unit a human authorized forgoing verification for — the EXPLICIT verdict, with no <c>acceptancePassed</c> beside it (exactly how the co-sign overlay writes it).</summary>
    private static object Waived() =>
        new { agentRunId = Guid.NewGuid(), status = "Succeeded", acceptanceVerdict = VerificationDisposition.Waived, changedFiles = new[] { "a.cs" } };

    private static object Priced(int inputTokens, int outputTokens) =>
        new { agentRunId = Guid.NewGuid(), status = "Failed", acceptancePassed = false, acceptanceDetail = "tests-failed-exit-1", model = "fixture-model", inputTokens, outputTokens, changedFiles = new[] { "a.cs" } };

    private static object FailedWithScale(string[] changedFiles, int? totalChangedFiles, string[] repositoryAliases) =>
        new
        {
            agentRunId = Guid.NewGuid(),
            status = "Failed",
            acceptancePassed = false,
            acceptanceDetail = "tests-failed-exit-1",
            changedFiles,
            totalChangedFiles,
            repositoryResults = repositoryAliases.Select(alias => new { alias, repositoryId = Guid.NewGuid(), changedFiles = new[] { $"{alias}/a.cs" } }).ToArray(),
        };

    private static SupervisorPriorDecision Prior(int seq, string kind, string payloadJson, string? outcomeJson = null) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = seq,
        Status = SupervisorDecisionStatus.Succeeded,
        DecisionKind = kind,
        PayloadJson = payloadJson,
        OutcomeJson = outcomeJson,
    };
}
