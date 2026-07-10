using System.Text.Json;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The S3 plan-confirmation gate's PURE detection logic (<see cref="SupervisorPlanConfirmation"/>) — the tape
/// truth table both the turn loop's injection and the confirm endpoint's card lookup ride on, plus the answer
/// composition that keeps a Request-changes click from ever reading as an approval.
/// </summary>
public class SupervisorPlanConfirmationTests
{
    [Fact]
    public void The_confirmation_marker_is_pinned()
    {
        // The marker is the card-recognition contract (the gate + the confirm endpoint both match on it) AND the
        // reply instruction the human reads. Rewording it silently orphans every in-flight parked confirmation.
        SupervisorPlanConfirmation.ConfirmationMarker.ShouldBe("Reply 'approve' to run this plan, or describe the changes you want.");
    }

    [Fact]
    public void IntoAskHuman_authors_a_marker_carrying_question_naming_the_version_and_size()
    {
        var decision = SupervisorPlanConfirmation.IntoAskHuman(planVersion: 2, itemCount: 3, delivery: null, priorApprovedDelivery: null);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);

        var question = JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("question").GetString()!;
        question.ShouldContain("plan v2");
        question.ShouldContain("3 step(s)");
        question.ShouldContain(SupervisorPlanConfirmation.ConfirmationMarker);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntoAskHuman_is_deterministic_for_a_replayed_turn(bool viaCard)
    {
        var a = SupervisorPlanConfirmation.IntoAskHuman(1, 2, null, null);
        var b = SupervisorPlanConfirmation.IntoAskHuman(1, 2, null, null);

        (viaCard ? a.PayloadJson : a.Kind).ShouldBe(viaCard ? b.PayloadJson : b.Kind, "a crash-replay must re-derive byte-identical card bytes → the same idempotency key");
    }

    // ── DC-1/DC-2a: the card names the EFFECTIVE delivery contract before the operator approves ──

    [Fact]
    public void IntoAskHuman_names_an_auto_opened_pull_request_and_its_target_branch()
    {
        var decision = SupervisorPlanConfirmation.IntoAskHuman(1, 2, new DeliverySpec { OpenPullRequest = true, TargetBranch = "release" }, priorApprovedDelivery: null);

        var question = JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("question").GetString()!;
        question.ShouldContain("automatically open a pull request against release", customMessage: "the operator must see the side-effecting behaviour BEFORE approving the plan");
    }

    [Fact]
    public void IntoAskHuman_names_the_default_branch_when_none_was_specified()
    {
        var decision = SupervisorPlanConfirmation.IntoAskHuman(1, 2, new DeliverySpec { OpenPullRequest = true }, priorApprovedDelivery: null);

        var question = JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("question").GetString()!;
        question.ShouldContain("the repository's default branch");
    }

    [Fact]
    public void IntoAskHuman_names_nothing_extra_when_no_contract_exists_on_either_side()
    {
        var decision = SupervisorPlanConfirmation.IntoAskHuman(1, 2, delivery: null, priorApprovedDelivery: null);

        var question = JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("question").GetString()!;
        question.ShouldNotContain("pull request", customMessage: "no contract at all is byte-identical to pre-DC-1 — it needs no extra confirmation attention");
        question.ShouldContain(SupervisorPlanConfirmation.ConfirmationMarker, customMessage: "the marker must still be intact regardless of the delivery summary");
    }

    [Fact]
    public void IntoAskHuman_states_an_explicit_decline_instead_of_rendering_nothing()
    {
        // Requirement (b): an explicit "don't open a PR" must be VISIBLE on the card — a silent "" would let the
        // card promise nothing while the actual stop behaviour explicitly withholds the PR.
        var decision = SupervisorPlanConfirmation.IntoAskHuman(1, 2, new DeliverySpec { OpenPullRequest = false }, priorApprovedDelivery: null);

        var question = JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("question").GetString()!;
        question.ShouldContain("will NOT automatically open a pull request", customMessage: "an explicit decline must be named, not silently omitted");
    }

    [Fact]
    public void IntoAskHuman_flags_a_revocation_when_a_replan_drops_an_already_approved_pull_request()
    {
        // The prior plan's PR was approved; THIS plan proposes nothing — the card must flag the downgrade instead
        // of silently letting the operator believe the earlier promise still stands.
        var priorApproved = new DeliverySpec { OpenPullRequest = true, TargetBranch = "main" };

        var decision = SupervisorPlanConfirmation.IntoAskHuman(2, 1, delivery: null, priorApprovedDelivery: priorApproved);

        var question = JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("question").GetString()!;
        question.ShouldContain("REVOKES", customMessage: "the operator must see the downgrade BEFORE approving the weaker plan");
    }

    [Fact]
    public void IntoAskHuman_prefers_naming_the_current_pr_over_the_revocation_when_the_replan_restores_it()
    {
        // A plan that itself opens a PR again is not a revocation, even with a prior approval sitting behind it.
        var priorApproved = new DeliverySpec { OpenPullRequest = true, TargetBranch = "main" };

        var decision = SupervisorPlanConfirmation.IntoAskHuman(3, 1, new DeliverySpec { OpenPullRequest = true, TargetBranch = "main" }, priorApprovedDelivery: priorApproved);

        var question = JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("question").GetString()!;
        question.ShouldContain("automatically open a pull request against main");
        question.ShouldNotContain("REVOKES");
    }

    // ── LastApprovedDelivery: the running "was in effect" baseline a downgrade compares against ──

    [Fact]
    public void LastApprovedDelivery_is_null_when_nothing_was_ever_approved() =>
        SupervisorPlanConfirmation.LastApprovedDelivery(Array.Empty<SupervisorPriorDecision>()).ShouldBeNull();

    [Fact]
    public void LastApprovedDelivery_ignores_the_latest_plan_which_has_no_card_yet()
    {
        var priors = new[] { PlanWithDelivery(true) };

        SupervisorPlanConfirmation.LastApprovedDelivery(priors).ShouldBeNull("the current plan's own contract is not yet approved by anything — it must never count as the baseline");
    }

    [Fact]
    public void LastApprovedDelivery_commits_the_plans_delivery_once_its_card_is_approved()
    {
        var priors = new[] { PlanWithDelivery(true), ConfirmationCard(answer: "approve") };

        var approved = SupervisorPlanConfirmation.LastApprovedDelivery(priors);
        approved.ShouldNotBeNull();
        approved!.OpenPullRequest.ShouldBe(true);
    }

    [Fact]
    public void LastApprovedDelivery_never_commits_a_rejected_plans_delivery()
    {
        var priors = new[] { PlanWithDelivery(true), ConfirmationCard(answer: "revise: smaller steps") };

        SupervisorPlanConfirmation.LastApprovedDelivery(priors).ShouldBeNull("a rejected plan's contract was never in effect");
    }

    [Fact]
    public void LastApprovedDelivery_tracks_the_most_recently_approved_version_across_multiple_replans()
    {
        var priors = new[]
        {
            PlanWithDelivery(true), ConfirmationCard(answer: "approve"),
            PlanWithDelivery(false), ConfirmationCard(answer: "approve"),
        };

        var approved = SupervisorPlanConfirmation.LastApprovedDelivery(priors);
        approved.ShouldNotBeNull();
        approved!.OpenPullRequest.ShouldBe(false, "the SECOND approval is the current baseline — it supersedes the first");
    }

    // ── LatestPlanDecision ──

    [Fact]
    public void LatestPlanDecision_returns_the_newest_plan_when_a_re_plan_exists()
    {
        var v1 = Decision(SupervisorDecisionKinds.Plan, """{"goal":"g","subtasks":[]}""", outcomeJson: "{}");
        var v2 = Decision(SupervisorDecisionKinds.Plan, """{"goal":"g2","subtasks":[]}""", outcomeJson: "{}");

        SupervisorPlanConfirmation.LatestPlanDecision(new[] { v1, Spawn(), v2 }).ShouldBe(v2);
    }

    [Fact]
    public void LatestPlanDecision_returns_null_when_no_plan_exists() =>
        SupervisorPlanConfirmation.LatestPlanDecision(new[] { Spawn() }).ShouldBeNull();

    // ── NeedsConfirmation: anchored on the LATEST plan, looking for this gate's own card after it ──

    [Fact]
    public void No_plan_on_the_tape_needs_nothing() =>
        SupervisorPlanConfirmation.NeedsConfirmation(Context()).ShouldBeFalse();

    [Fact]
    public void A_fresh_plan_with_no_card_after_it_needs_confirmation() =>
        SupervisorPlanConfirmation.NeedsConfirmation(Context(Plan())).ShouldBeTrue();

    [Fact]
    public void A_plan_already_followed_by_its_card_does_not_ask_again() =>
        SupervisorPlanConfirmation.NeedsConfirmation(Context(Plan(), ConfirmationCard())).ShouldBeFalse();

    [Fact]
    public void A_degraded_no_surface_card_never_satisfies_the_gate()
    {
        // The degraded card parked nothing and asked no one — counting it would let a surface-less run
        // spawn an unconfirmed plan (the fail-open the review caught).
        SupervisorPlanConfirmation.NeedsConfirmation(Context(Plan(), DegradedConfirmationCard())).ShouldBeTrue();
    }

    [Fact]
    public void A_content_ask_after_the_plan_is_not_a_confirmation()
    {
        // The decider's own question card never satisfies the gate — only the gate's marker-carrying card does.
        SupervisorPlanConfirmation.NeedsConfirmation(Context(Plan(), ContentAsk())).ShouldBeTrue();
    }

    [Fact]
    public void An_approval_gate_card_is_not_a_confirmation()
    {
        // The E5 side-effect APPROVAL card carries a DIFFERENT marker — the two gates never claim each other's cards.
        SupervisorPlanConfirmation.NeedsConfirmation(Context(Plan(), Ask($"Approve spawning 2 agent(s)? {SupervisorApprovalRequest.ApprovalMarker}", answer: "approve"))).ShouldBeTrue();
    }

    [Fact]
    public void A_revised_plan_regates()
    {
        // v1 was confirmed/answered, then the decider authored v2 — the scan anchors on v2, the old card is before it.
        SupervisorPlanConfirmation.NeedsConfirmation(Context(Plan(), ConfirmationCard(answer: "revise: merge"), Plan())).ShouldBeTrue();
    }

    [Fact]
    public void A_released_plan_with_later_work_never_reasks() =>
        SupervisorPlanConfirmation.NeedsConfirmation(Context(Plan(), ConfirmationCard(answer: "approve"), Spawn())).ShouldBeFalse();

    // ── WasJustAnswered: the once-only release edge (position-bound to the LAST decision) ──

    [Theory]
    [InlineData("approve", true)]
    [InlineData("  Approve — but keep step 2 small", true)]
    [InlineData("APPROVED", true)]
    [InlineData("revise: merge the steps", false)]
    [InlineData("merge the steps into one", false)]
    public void A_just_answered_card_releases_with_the_answers_verdict(string answer, bool expectApproved)
    {
        SupervisorPlanConfirmation.WasJustAnswered(Context(Plan(), ConfirmationCard(answer)), out var approved).ShouldBeTrue();

        approved.ShouldBe(expectApproved);
    }

    [Fact]
    public void An_unanswered_card_does_not_release() =>
        SupervisorPlanConfirmation.WasJustAnswered(Context(Plan(), ConfirmationCard(answer: null)), out _).ShouldBeFalse();

    [Fact]
    public void A_card_that_is_not_the_last_decision_does_not_refire()
    {
        // On the turn AFTER release the last decision is the spawn — the status flip must not re-fire forever.
        SupervisorPlanConfirmation.WasJustAnswered(Context(Plan(), ConfirmationCard(answer: "approve"), Spawn()), out _).ShouldBeFalse();
    }

    [Fact]
    public void A_content_ask_answer_is_not_a_confirmation_release() =>
        SupervisorPlanConfirmation.WasJustAnswered(Context(Plan(), ContentAsk(answer: "patch it")), out _).ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"question\":42}")]
    public void Malformed_payloads_never_match_the_card(string? payloadJson) =>
        SupervisorPlanConfirmation.QuestionCarriesMarker(payloadJson).ShouldBeFalse();

    // ── LatestPlanRejected: the structural never-execute-a-rejected-plan floor ──

    [Fact]
    public void A_feedback_answer_marks_the_latest_plan_rejected() =>
        SupervisorPlanConfirmation.LatestPlanRejected(Context(Plan(), ConfirmationCard(answer: "revise: merge the steps"))).ShouldBeTrue();

    [Fact]
    public void The_rejection_holds_even_after_later_non_plan_decisions() =>
        SupervisorPlanConfirmation.LatestPlanRejected(Context(Plan(), ConfirmationCard(answer: "revise: merge"), Spawn())).ShouldBeTrue();

    [Theory]
    [InlineData("approve")]
    [InlineData(null)]
    public void An_approved_or_unanswered_card_is_not_a_rejection(string? answer) =>
        SupervisorPlanConfirmation.LatestPlanRejected(Context(Plan(), ConfirmationCard(answer))).ShouldBeFalse();

    [Fact]
    public void A_revised_plan_clears_the_rejection() =>
        SupervisorPlanConfirmation.LatestPlanRejected(Context(Plan(), ConfirmationCard(answer: "revise: merge"), Plan())).ShouldBeFalse();

    [Fact]
    public void An_answered_confirmation_card_counts_as_progress_but_a_pending_or_content_one_does_not()
    {
        SupervisorPlanConfirmation.IsAnsweredConfirmationCard(ConfirmationCard(answer: "revise: split step 2")).ShouldBeTrue();
        SupervisorPlanConfirmation.IsAnsweredConfirmationCard(ConfirmationCard(answer: null)).ShouldBeFalse();
        SupervisorPlanConfirmation.IsAnsweredConfirmationCard(ContentAsk(answer: "patch it")).ShouldBeFalse();
    }

    // ── ComposeAnswer: the confirm endpoint's folded-answer contract ──

    [Theory]
    [InlineData(true, null, "approve")]
    [InlineData(true, "  keep step 2 small ", "approve — keep step 2 small")]
    [InlineData(false, "merge the steps", "merge the steps")]
    [InlineData(false, "Approve nothing until QA signs off", "revise: Approve nothing until QA signs off")]
    public void ComposeAnswer_leads_approvals_with_the_approve_word_and_never_lets_feedback_read_as_one(bool approve, string? feedback, string expected) =>
        WorkPlanConfirmationService.ComposeAnswer(approve, feedback).ShouldBe(expected);

    [Fact]
    public void The_revision_prefix_is_pinned() =>
        WorkPlanConfirmationService.RevisionPrefix.ShouldBe("revise: ");

    [Fact]
    public void A_non_approve_answer_requires_feedback() =>
        Should.Throw<ArgumentException>(() => WorkPlanConfirmationService.ComposeAnswer(approve: false, feedback: "  "));

    // ─── fixtures ────────────────────────────────────────────────────────────────

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] priors) => new()
    {
        Goal = "ship it",
        TeamId = Guid.NewGuid(),
        PriorDecisions = priors,
        RequirePlanConfirmation = true,
    };

    private static SupervisorPriorDecision Plan() => Decision(SupervisorDecisionKinds.Plan, "{\"goal\":\"g\",\"subtasks\":[]}", outcomeJson: "{}");

    /// <summary>A Plan decision whose own (already-clamped, DC-1 shape) payload carries a delivery contract — mirrors what <c>ClampPlanDelivery</c> freezes onto a real plan's payload before persist.</summary>
    private static SupervisorPriorDecision PlanWithDelivery(bool openPullRequest) =>
        Decision(SupervisorDecisionKinds.Plan, $"{{\"goal\":\"g\",\"subtasks\":[],\"delivery\":{{\"openPullRequest\":{(openPullRequest ? "true" : "false")}}}}}", outcomeJson: "{}");

    private static SupervisorPriorDecision Spawn() => Decision(SupervisorDecisionKinds.Spawn, "{\"subtaskIds\":[\"sa\"]}", outcomeJson: "{}");

    private static SupervisorPriorDecision ConfirmationCard(string? answer = null)
    {
        var card = SupervisorPlanConfirmation.IntoAskHuman(1, 2, null, null);
        return Decision(card.Kind, card.PayloadJson, AskOutcome(answer));
    }

    /// <summary>The DEGRADED no-surface card: marker-carrying payload, but the outcome recorded neither a wait token nor an answer (RealSupervisorActionExecutor's no-conversation self-advance).</summary>
    private static SupervisorPriorDecision DegradedConfirmationCard()
    {
        var card = SupervisorPlanConfirmation.IntoAskHuman(1, 2, null, null);
        return Decision(card.Kind, card.PayloadJson, JsonSerializer.Serialize(new { question = "q", askHuman = "no-conversation", answer = (string?)null }));
    }

    private static SupervisorPriorDecision ContentAsk(string? answer = null) =>
        Ask("which approach: rewrite or patch?", answer);

    private static SupervisorPriorDecision Ask(string question, string? answer) =>
        Decision(SupervisorDecisionKinds.AskHuman, JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = question }), AskOutcome(answer));

    private static string AskOutcome(string? answer) =>
        JsonSerializer.Serialize(new { question = "q", askHumanToken = "tok", answer });

    private static SupervisorPriorDecision Decision(string kind, string payloadJson, string? outcomeJson) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = 0,
        DecisionKind = kind,
        Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = payloadJson,
        OutcomeJson = outcomeJson,
    };
}
