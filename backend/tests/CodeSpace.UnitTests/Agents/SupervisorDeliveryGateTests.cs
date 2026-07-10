using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: DC-2b (deliver-at-stop enforcement) — <see cref="SupervisorDeliveryGate.Validate"/>, pinned WITHOUT a
/// DB. Proves the owner-locked authorization ladder: nothing wants a PR ⇒ untouched; wants one but UNAUTHORIZED
/// (a pure model proposal, never confirmed and never operator-declared) ⇒ ask_human, park; wants one AND
/// authorized (① a confirmed plan, or ② the operator's own declaration) with no prior attempt ⇒ a
/// server-authored <c>publish</c>; a prior attempt that fully succeeded ⇒ untouched; a prior attempt with ANY
/// failed target ⇒ ask_human naming the diagnosis, never a blind retry.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDeliveryGateTests
{
    // ── Nothing wants a PR — byte-identical to pre-DC-2b ──────────────────────────────

    [Fact]
    public void A_stop_with_no_delivery_contract_at_all_is_untouched() =>
        SupervisorDeliveryGate.Validate(Context(), StopDecision()).ShouldBeNull();

    [Fact]
    public void A_stop_whose_latest_plan_explicitly_declines_a_pr_is_untouched()
    {
        var context = Context(deliverySpec: null, Plan(1, openPullRequest: false));

        SupervisorDeliveryGate.Validate(context, StopDecision()).ShouldBeNull("an explicit decline is the safe default — nothing to enforce");
    }

    // ── Wants a PR but UNAUTHORIZED — a pure model proposal, never confirmed ──────────

    [Fact]
    public void A_stop_whose_plan_proposes_true_with_no_confirmation_and_no_operator_declaration_parks()
    {
        var context = Context(deliverySpec: null, Plan(1, openPullRequest: true));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "a model proposal alone never authorizes opening a PR");

        var question = JsonSerializer.Deserialize<SupervisorAskHumanPayload>(substituted.PayloadJson, AgentJson.Options)!.Question;
        question.ShouldContain("never confirmed", Case.Insensitive);
    }

    [Fact]
    public void A_stop_whose_plan_proposes_true_and_was_REJECTED_via_card_still_parks()
    {
        var context = Context(deliverySpec: null, Plan(1, openPullRequest: true), ConfirmationCard(answer: "revise: smaller steps"));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "a rejected plan's proposal was never approved — it still counts as unauthorized");
    }

    // ── Path ①: a CONFIRMED plan naming a PR authorizes it ────────────────────────────

    [Fact]
    public void A_stop_whose_plan_proposes_true_and_was_APPROVED_via_card_substitutes_a_publish()
    {
        var context = Context(deliverySpec: null, Plan(1, openPullRequest: true), ConfirmationCard(answer: "approve"));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Publish);
    }

    // ── Path ②: the operator's OWN declaration authorizes it regardless of confirmation ──

    [Fact]
    public void A_stop_whose_operator_declared_true_substitutes_a_publish_with_no_confirmation_at_all()
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true }, Plan(1, openPullRequest: true));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Publish, "the operator's own pre-declaration authorizes it independent of any plan-confirmation gate");
    }

    // ── First attempt — substitute a server-authored publish ─────────────────────────

    [Fact]
    public void The_forced_publish_is_server_authored_and_carries_the_rejected_stops_own_summary_forward()
    {
        // Adversarial-sweep finding: the rejected stop's summary never reaches context.PriorDecisions (it was
        // substituted away, never persisted) — the ONLY way the eventual PR's title/body can recover the model's
        // own account of the work is if THIS decision carries it forward explicitly.
        var context = Context(new DeliverySpec { OpenPullRequest = true }, Plan(1, openPullRequest: true));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Publish);
        JsonSerializer.Deserialize<SupervisorPublishPayload>(substituted.PayloadJson, AgentJson.Options)!.StopSummary.ShouldBe("done");
    }

    // ── DC-2d: the substituted publish carries the SAME TargetBranch the card showed ──

    [Fact]
    public void The_forced_publish_carries_the_confirmed_plans_own_target_branch()
    {
        // DC-2d sweep finding: the card (SupervisorPlanConfirmation.DeliverySummary) names the PLAN's own clamped
        // TargetBranch, but execution used to read ONLY the operator's raw launch-time spec — a model-proposed
        // branch the operator never set would show on the card, then silently vanish at execution. The gate must
        // resolve the SAME plan payload the card read from and hand it to the executor explicitly.
        var context = Context(deliverySpec: null, Plan(1, openPullRequest: true, targetBranch: "release"), ConfirmationCard(answer: "approve"));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Publish);
        JsonSerializer.Deserialize<SupervisorPublishPayload>(substituted.PayloadJson, AgentJson.Options)!.TargetBranch.ShouldBe("release");
    }

    [Fact]
    public void The_forced_publish_falls_back_to_the_operators_own_target_branch_when_the_plan_named_none()
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true, TargetBranch = "staging" }, Plan(1, openPullRequest: true, targetBranch: null));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        JsonSerializer.Deserialize<SupervisorPublishPayload>(substituted!.PayloadJson, AgentJson.Options)!.TargetBranch.ShouldBe("staging");
    }

    [Fact]
    public void The_forced_publish_never_leaks_a_rejected_newer_plans_target_branch_when_an_older_plan_is_still_the_approved_baseline()
    {
        // Post-merge adversarial-sweep finding: EffectiveDelivery used to read TargetBranch off the tape's LATEST
        // plan regardless of confirmation — but authorization (IsAuthorized) checks LastApprovedDelivery, which
        // can be an OLDER, genuinely approved plan. A later plan revision proposing a DIFFERENT branch that gets
        // REJECTED must never have its branch leak into the opened PR — the branch must come from the SAME source
        // that authorized the publish in the first place.
        var context = Context(deliverySpec: null,
            Plan(1, openPullRequest: true, targetBranch: "release"),
            ConfirmationCard(answer: "approve"),
            Plan(2, openPullRequest: true, targetBranch: "staging"),
            ConfirmationCard(answer: "revise: smaller steps please"));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Publish, "the OLDER plan's approval still authorizes — a later REJECTED revision doesn't revoke it");
        JsonSerializer.Deserialize<SupervisorPublishPayload>(substituted.PayloadJson, AgentJson.Options)!.TargetBranch
            .ShouldBe("release", "the branch must come from the ONE approved card, never a later rejected proposal");
    }

    // ── A prior publish attempt already ran ───────────────────────────────────────────

    [Theory]
    [InlineData(RoomPullRequestDisposition.Opened)]
    [InlineData(RoomPullRequestDisposition.AlreadyOpened)]
    [InlineData(RoomPullRequestDisposition.Skipped)]
    public void A_stop_after_a_fully_satisfied_publish_attempt_is_untouched(RoomPullRequestDisposition disposition)
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, PublishOutcome(disposition)));

        SupervisorDeliveryGate.Validate(context, StopDecision()).ShouldBeNull("every target is satisfied — nothing left to enforce, let stop through");
    }

    [Fact]
    public void A_stop_after_a_publish_attempt_with_a_failed_target_parks_naming_the_diagnosis()
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, PublishOutcome(RoomPullRequestDisposition.Failed, error: "the provider rejected the request")));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "a diagnosed failure never blind-retries");

        var question = JsonSerializer.Deserialize<SupervisorAskHumanPayload>(substituted.PayloadJson, AgentJson.Options)!.Question;
        question.ShouldContain("the provider rejected the request");
    }

    [Fact]
    public void A_stop_never_re_substitutes_a_second_forced_publish_after_the_first_one_failed()
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, PublishOutcome(RoomPullRequestDisposition.Failed, error: "boom")));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "exactly one auto-publish attempt — the second stop attempt must park, never loop publish→publish→publish");
    }

    // ── Adversarial-sweep finding: a SECOND, genuinely new round of work after an already-satisfied publish ──

    [Fact]
    public void A_second_round_of_work_merged_after_a_successful_publish_gets_its_own_fresh_publish_attempt()
    {
        // Round 1 published cleanly. The run then did MORE work (a later merge produced a genuinely new branch) —
        // the OLD publish's "every target satisfied" verdict describes round 1's branch, not round 2's. Trusting
        // it here would silently skip opening a PR for round 2's authorized, newly-published work.
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, PublishOutcome(RoomPullRequestDisposition.Opened)),
            Decision(SupervisorDecisionKinds.Merge, 3, "{}"));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Publish, "a merge landed AFTER the last publish attempt — its old verdict is stale, so this must re-attempt, not trust it as already satisfied");
    }

    [Fact]
    public void A_second_round_of_spawned_work_after_a_successful_publish_also_gets_a_fresh_attempt()
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, PublishOutcome(RoomPullRequestDisposition.Opened)),
            Decision(SupervisorDecisionKinds.Spawn, 3, "{}"));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Publish, "a new spawn after the last publish attempt also invalidates its stale verdict");
    }

    [Fact]
    public void A_failed_publish_followed_by_fresh_merged_work_gets_a_new_attempt_instead_of_permanently_parking()
    {
        // The mirror of the "never re-substitute after a failure" test above: THAT test has nothing after the
        // failed publish (correctly never retries blindly); THIS one has genuinely new work after it, which must
        // buy a fresh attempt — a transient failure must not permanently strand a run that later made real progress.
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, PublishOutcome(RoomPullRequestDisposition.Failed, error: "boom")),
            Decision(SupervisorDecisionKinds.Merge, 3, "{}"));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Publish);
    }

    // ── Every non-stop decision is always untouched ───────────────────────────────────

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan)]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    [InlineData(SupervisorDecisionKinds.Merge)]
    [InlineData(SupervisorDecisionKinds.Resolve)]
    [InlineData(SupervisorDecisionKinds.AskHuman)]
    [InlineData(SupervisorDecisionKinds.Publish)]
    public void A_non_stop_decision_is_always_untouched_regardless_of_tape_state(string kind)
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true }, Plan(1, openPullRequest: true));

        SupervisorDeliveryGate.Validate(context, new SupervisorDecision { Kind = kind, PayloadJson = "{}" }).ShouldBeNull();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────

    private static SupervisorTurnContext Context(DeliverySpec? deliverySpec, params SupervisorPriorDecision[] prior) => new()
    {
        Goal = "ship", SupervisorRunId = Guid.NewGuid(), TeamId = Guid.NewGuid(), NodeId = "sup", TurnNumber = prior.Length + 1,
        PriorDecisions = prior, DeliverySpec = deliverySpec,
    };

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] prior) => Context(deliverySpec: null, prior);

    /// <summary>A Plan decision whose own PAYLOAD (not outcome) carries a delivery contract — <c>ReadPlanDelivery</c> reads the PAYLOAD (the model's authored — here already-clamped — proposal), mirroring what <c>ClampPlanDelivery</c> freezes onto a real plan before persist.</summary>
    private static SupervisorPriorDecision Plan(long sequence, bool openPullRequest, string? targetBranch = null)
    {
        var delivery = new DeliverySpec { OpenPullRequest = openPullRequest, TargetBranch = targetBranch };

        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new SupervisorPlanPayload { Goal = "g", Delivery = delivery }, AgentJson.Options),
            OutcomeJson = "{}",
        };
    }

    /// <summary>The S3 confirmation card: its QUESTION (marker-carrying) lives on the PAYLOAD, its ANSWER on the outcome — mirroring <see cref="SupervisorPlanConfirmation.IntoAskHuman"/>'s real shape.</summary>
    private static SupervisorPriorDecision ConfirmationCard(string? answer) => new()
    {
        Id = Guid.NewGuid(), Sequence = 99, DecisionKind = SupervisorDecisionKinds.AskHuman, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = $"confirm? {SupervisorPlanConfirmation.ConfirmationMarker}" }, AgentJson.Options),
        OutcomeJson = JsonSerializer.Serialize(new { question = $"confirm? {SupervisorPlanConfirmation.ConfirmationMarker}", askHumanToken = "tok", answer }, AgentJson.Options),
    };

    private static SupervisorPriorDecision Decision(string kind, long sequence, string? outcomeJson) =>
        new() { Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcomeJson };

    private static SupervisorDecision StopDecision() => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "completed", Summary = "done" }, AgentJson.Options),
    };

    private static string PublishOutcome(RoomPullRequestDisposition disposition, string? error = null) =>
        JsonSerializer.Serialize(new RoomPullRequestResult
        {
            PullRequests = new[] { new RoomPullRequestOpened { Alias = "primary", Disposition = disposition, Error = error } },
        }, AgentJson.Options);
}
