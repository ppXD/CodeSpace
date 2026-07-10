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
    public void A_stop_after_a_fully_satisfied_publish_attempt_is_untouched(RoomPullRequestDisposition disposition)
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, PublishOutcome(disposition)));

        SupervisorDeliveryGate.Validate(context, StopDecision()).ShouldBeNull("a real PR exists for every non-failed target — nothing left to enforce, let stop through");
    }

    // ── H1 (vacuous-success fix): an EMPTY publish result is NOT satisfaction ─────────

    [Fact]
    public void A_stop_after_a_publish_attempt_that_resolved_zero_targets_parks_instead_of_vacuously_passing()
    {
        // The verified false-green chain: empty-handed stop passes I3 → this gate forces a publish → the branch
        // resolver finds NOTHING to open a PR from → the opener returns PullRequests=[] → the old satisfaction
        // rung filtered ONLY Failed, so the empty set passed vacuously and a required-PR run completed with zero
        // PRs. An empty result must park: the contract is UNSATISFIED, not satisfied-by-absence.
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, EmptyPublishOutcome()));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted.ShouldNotBeNull("an empty publish result satisfies nothing — vacuous success is the exact hole this gate exists to close");
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);

        var question = JsonSerializer.Deserialize<SupervisorAskHumanPayload>(substituted.PayloadJson, AgentJson.Options)!.Question;
        question.ShouldContain("no published branch", Case.Insensitive);
    }

    [Fact]
    public void A_missing_publish_outcome_parks_the_same_as_an_empty_one()
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, outcomeJson: "{}"));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "an unreadable/absent result proves nothing was delivered — fail closed, never fail open");
    }

    // ── H1 (Skipped-as-satisfied fix): policy-skipped is a HUMAN decision, not satisfaction ──

    [Fact]
    public void A_stop_after_an_all_skipped_publish_attempt_parks_naming_the_patch_only_policy()
    {
        // PatchOnly repos yield Skipped by deliberate policy — but the operator ALSO required a PR. Two operator
        // intents conflict; the gate must surface the conflict to a human, never silently pick "no PR" and call
        // the contract satisfied. (The full WaivedByPolicy state with recorded authority lands in Phase T — the
        // interim waiver is the human's answer to THIS card.)
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, PublishOutcome(RoomPullRequestDisposition.Skipped)));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted.ShouldNotBeNull();
        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);

        var question = JsonSerializer.Deserialize<SupervisorAskHumanPayload>(substituted.PayloadJson, AgentJson.Options)!.Question;
        question.ShouldContain("patch-only", Case.Insensitive);
    }

    [Fact]
    public void A_mixed_opened_and_skipped_publish_attempt_still_satisfies()
    {
        // Multi-repo: one repo got its PR, a sibling is patch-only. Something REAL was delivered against the
        // contract — the skipped sibling is policy-consistent, not a vacuous pass.
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, PublishOutcome(
                new RoomPullRequestOpened { Alias = "primary", Disposition = RoomPullRequestDisposition.Opened },
                new RoomPullRequestOpened { Alias = "docs", Disposition = RoomPullRequestDisposition.Skipped })));

        SupervisorDeliveryGate.Validate(context, StopDecision()).ShouldBeNull();
    }

    // ── H1 (adjudication): an answered Delivery-gate card buys ONE re-attempt, then stands as the waiver ──
    //
    // Without this, every park below is a dead end: the human answers, the model stops again, the gate re-parks
    // on the identical tape state forever (the audit's own "re-parks every stop" finding). The answer is
    // deliberately CONTENT-BLIND — never parsed into an authorization (that would be the prefix-matching
    // laundering hole). Because the card invites FIXING the blocker and only this gate can re-issue a publish,
    // an answer first buys exactly one fresh server re-attempt (an answer that fixed the world produces the PR);
    // only a re-check that is STILL unsatisfied lets the answer stand as the interim waiver and release the
    // stop. Structured waivers with recorded authority replace this in Phase T.

    [Fact]
    public void An_answered_gate_card_after_an_unsatisfied_publish_buys_one_fresh_re_attempt()
    {
        // e.g. the human flipped the repo off patch-only and answered "retry" — the ONLY actor that can re-issue
        // a publish is this gate, so the answer must produce a re-attempt, never a direct completion waiver.
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, EmptyPublishOutcome()),
            GateCard(3, answer: "fixed the blocker — retry"));

        SupervisorDeliveryGate.Validate(context, StopDecision())!.Kind.ShouldBe(SupervisorDecisionKinds.Publish);
    }

    [Fact]
    public void A_failed_publish_with_an_answered_gate_card_re_attempts_instead_of_re_parking_forever()
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, PublishOutcome(RoomPullRequestDisposition.Failed, error: "boom")),
            GateCard(3, answer: "provider is back up, go"));

        SupervisorDeliveryGate.Validate(context, StopDecision())!.Kind.ShouldBe(SupervisorDecisionKinds.Publish);
    }

    [Fact]
    public void A_still_unsatisfied_re_check_after_an_answered_card_releases_the_stop()
    {
        // The full adjudication arc: publish found nothing → card answered → ONE re-check ran → still nothing.
        // The human's answer now stands as the interim waiver — the run completes without the PR instead of
        // cycling park→answer→park forever.
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, EmptyPublishOutcome()),
            GateCard(3, answer: "understood — finish without the PR"),
            Decision(SupervisorDecisionKinds.Publish, 4, EmptyPublishOutcome()));

        SupervisorDeliveryGate.Validate(context, StopDecision()).ShouldBeNull("adjudicated AND re-verified — the waiver stands");
    }

    [Fact]
    public void A_re_check_that_still_fails_after_an_answered_card_also_releases()
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, PublishOutcome(RoomPullRequestDisposition.Failed, error: "boom")),
            GateCard(3, answer: "give up on the PR then"),
            Decision(SupervisorDecisionKinds.Publish, 4, PublishOutcome(RoomPullRequestDisposition.Failed, error: "boom")));

        SupervisorDeliveryGate.Validate(context, StopDecision()).ShouldBeNull("one adjudicated re-attempt is the bound — the Room button remains for a manual open afterwards");
    }

    [Fact]
    public void A_re_check_that_SUCCEEDS_after_an_answered_card_satisfies_normally()
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, EmptyPublishOutcome()),
            GateCard(3, answer: "pushed the branch — retry"),
            Decision(SupervisorDecisionKinds.Publish, 4, PublishOutcome(RoomPullRequestDisposition.Opened)));

        SupervisorDeliveryGate.Validate(context, StopDecision()).ShouldBeNull("the re-attempt opened the real PR — the contract is genuinely satisfied, not waived");
    }

    [Fact]
    public void An_answered_gate_card_invalidated_by_fresh_work_before_the_publish_does_not_release()
    {
        // The answer adjudicated an OLDER state; a merge then moved the world and the publish after it produced
        // a fresh verdict the human has never seen. Stale adjudication must not leak forward.
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            GateCard(2, answer: "fine"),
            Decision(SupervisorDecisionKinds.Merge, 3, "{}"),
            Decision(SupervisorDecisionKinds.Publish, 4, EmptyPublishOutcome()));

        SupervisorDeliveryGate.Validate(context, StopDecision())!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);
    }

    [Fact]
    public void An_unanswered_gate_card_does_not_release()
    {
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, EmptyPublishOutcome()),
            GateCard(3, answer: null));

        SupervisorDeliveryGate.Validate(context, StopDecision())!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);
    }

    [Fact]
    public void Fresh_work_after_an_answered_gate_card_gets_a_new_publish_attempt_not_a_stale_release()
    {
        // Adjudication covered round 1's emptiness; round 2 then merged REAL work. The stale release must not
        // let the stop skip round 2's own delivery obligation — the state-change rung outranks the release.
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, EmptyPublishOutcome()),
            GateCard(3, answer: "fine"),
            Decision(SupervisorDecisionKinds.Merge, 4, "{}"));

        SupervisorDeliveryGate.Validate(context, StopDecision())!.Kind.ShouldBe(SupervisorDecisionKinds.Publish);
    }

    [Fact]
    public void An_answered_unauthorized_park_releases_the_stop_but_never_substitutes_a_publish()
    {
        // The human answered the "never confirmed" card. A free-text answer is NOT parsed into an authorization
        // (content-blind release) — the stop passes, but no PR is auto-opened on the strength of prose.
        var context = Context(deliverySpec: null,
            Plan(1, openPullRequest: true),
            GateCard(2, answer: "ok, no PR needed"));

        SupervisorDeliveryGate.Validate(context, StopDecision()).ShouldBeNull("adjudicated — but only the stop is released; prose never becomes PR authorization");
    }

    [Fact]
    public void A_non_gate_ask_card_never_releases()
    {
        // Only THIS gate's own cards adjudicate its parks — an unrelated answered ask (a content question, a plan
        // confirmation) proves nothing about the delivery contract.
        var context = Context(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, EmptyPublishOutcome()),
            AskCard(3, question: "which auth provider should I use?", answer: "google"));

        SupervisorDeliveryGate.Validate(context, StopDecision())!.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);
    }

    // ── H1: no conversation surface — a park would be unanswerable, so force-stop with the diagnosis ──

    [Fact]
    public void An_unsatisfied_publish_with_no_conversation_surface_force_stops_with_the_delivery_diagnosis()
    {
        // Without a conversation, the substituted ask degrades to an unanswerable no-card self-advance and the
        // run would grind no-progress turns into a misleading NoProgress stop — burning decider calls to say the
        // wrong thing. Mirror GatePlanConfirmationAsync: stop immediately, naming the delivery reason.
        var context = NoConversationContext(new DeliverySpec { OpenPullRequest = true },
            Plan(1, openPullRequest: true),
            Decision(SupervisorDecisionKinds.Publish, 2, EmptyPublishOutcome()));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        SupervisorOutcome.ReadStopReason(substituted.PayloadJson).ShouldBe(SupervisorStopReasons.DeliveryAdjudicationUnavailable);
    }

    [Fact]
    public void An_unauthorized_contract_with_no_conversation_surface_also_force_stops_distinctly()
    {
        var context = NoConversationContext(deliverySpec: null, Plan(1, openPullRequest: true));

        var substituted = SupervisorDeliveryGate.Validate(context, StopDecision());

        substituted!.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        SupervisorOutcome.ReadStopReason(substituted.PayloadJson).ShouldBe(SupervisorStopReasons.DeliveryAdjudicationUnavailable);
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
        // A usable conversation surface is the common case — the no-surface force-stop has its own dedicated tests.
        ConversationId = Guid.NewGuid(),
    };

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] prior) => Context(deliverySpec: null, prior);

    private static SupervisorTurnContext NoConversationContext(DeliverySpec? deliverySpec, params SupervisorPriorDecision[] prior) =>
        Context(deliverySpec, prior) with { ConversationId = null };

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

    /// <summary>One of THIS gate's own parked cards (question carries the pinned prefix), answered or not.</summary>
    private static SupervisorPriorDecision GateCard(long sequence, string? answer) =>
        AskCard(sequence, question: $"{SupervisorDeliveryGate.QuestionPrefix}a pull request could not be opened — resolve", answer);

    private static SupervisorPriorDecision AskCard(long sequence, string question, string? answer) => new()
    {
        Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.AskHuman, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = question }, AgentJson.Options),
        OutcomeJson = JsonSerializer.Serialize(new { question, askHumanToken = "tok", answer }, AgentJson.Options),
    };

    private static string EmptyPublishOutcome() =>
        JsonSerializer.Serialize(new RoomPullRequestResult { PullRequests = Array.Empty<RoomPullRequestOpened>() }, AgentJson.Options);

    private static string PublishOutcome(params RoomPullRequestOpened[] pullRequests) =>
        JsonSerializer.Serialize(new RoomPullRequestResult { PullRequests = pullRequests }, AgentJson.Options);

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
