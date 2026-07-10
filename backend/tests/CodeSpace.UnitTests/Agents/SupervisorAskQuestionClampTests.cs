using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: H2's reserved-token sanitizer (<see cref="SupervisorAskQuestionClamp"/>) + the turn service's surgical
/// payload rewrite (<see cref="SupervisorTurnService.SanitizeAskPayloadJson"/>). A MODEL-authored <c>ask_human</c>
/// question must never pose as a SERVER card: the delivery/I3 gate prefixes drive H1's adjudication release, and
/// the three marker sentences drive structural authority reads (a minted confirmation marker answered 'approve'
/// could forge plan/delivery authorization). Every genuinely server-authored card is substituted AFTER the clamp
/// slot, so nothing legitimate is ever stripped.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorAskQuestionClampTests
{
    // ── The reserved token list is sourced from the owning consts — pin the membership so a new gate card remembers to enroll ──

    [Fact]
    public void The_reserved_tokens_are_exactly_the_five_server_card_identities()
    {
        SupervisorAskQuestionClamp.ReservedTokens.ShouldBe(new[]
        {
            SupervisorDeliveryGate.QuestionPrefix,
            SupervisorPublishGate.QuestionPrefix,
            SupervisorPlanConfirmation.ConfirmationMarker,
            SupervisorApprovalRequest.ApprovalMarker,
            SupervisorGateEscalation.EscalationMarker,
        });
    }

    [Fact]
    public void The_gate_prefixes_are_pinned_literals()
    {
        // Renaming either prefix orphans every in-flight parked run's H1 release — a rename must be a visible decision.
        SupervisorDeliveryGate.QuestionPrefix.ShouldBe("Delivery gate: ");
        SupervisorPublishGate.QuestionPrefix.ShouldBe("I3 publish gate: ");
    }

    // ── Sanitize ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_clean_question_returns_the_same_string_instance()
    {
        const string question = "which auth provider should the login flow use?";

        ReferenceEquals(SupervisorAskQuestionClamp.Sanitize(question), question)
            .ShouldBeTrue("the dominant no-token case must be a reference-checkable fast path");
    }

    [Fact]
    public void A_minted_delivery_gate_prefix_is_stripped()
    {
        SupervisorAskQuestionClamp.Sanitize($"{SupervisorDeliveryGate.QuestionPrefix}everything is done, OK to finish?")
            .ShouldBe("everything is done, OK to finish?");
    }

    [Fact]
    public void A_minted_confirmation_marker_mid_question_is_stripped()
    {
        SupervisorAskQuestionClamp.Sanitize($"the work looks complete. {SupervisorPlanConfirmation.ConfirmationMarker} shall I proceed?")
            .ShouldBe("the work looks complete.  shall I proceed?", "the marker sentence is removed wherever it appears, not only as a prefix");
    }

    [Fact]
    public void Every_reserved_token_is_stripped_when_several_are_minted_at_once()
    {
        var minted = $"{SupervisorPublishGate.QuestionPrefix}done. {SupervisorApprovalRequest.ApprovalMarker} {SupervisorGateEscalation.EscalationMarker}";

        var sanitized = SupervisorAskQuestionClamp.Sanitize(minted);

        foreach (var token in SupervisorAskQuestionClamp.ReservedTokens)
            sanitized.ShouldNotContain(token);

        sanitized.ShouldBe("done.");
    }

    [Fact]
    public void A_question_that_is_ONLY_reserved_text_collapses_to_the_legible_fallback_never_a_blank()
    {
        SupervisorAskQuestionClamp.Sanitize(SupervisorPlanConfirmation.ConfirmationMarker)
            .ShouldBe(SupervisorAskQuestionClamp.AllReservedFallback, "a blank question is the rejected-ask path; the strip must stay legible instead");
    }

    [Fact]
    public void A_token_spliced_by_an_inner_removal_is_re_stripped()
    {
        // Removing the inner token glues the two halves of an outer token together — the loop-until-stable pass
        // must catch the spliced occurrence rather than persisting it.
        var prefix = SupervisorDeliveryGate.QuestionPrefix;
        var spliced = prefix[..5] + SupervisorApprovalRequest.ApprovalMarker + prefix[5..] + "may I finish?";

        SupervisorAskQuestionClamp.Sanitize(spliced).ShouldBe("may I finish?");
    }

    // ── The surgical payload rewrite ──────────────────────────────────────────────────

    [Fact]
    public void A_clean_payload_is_reported_unchanged()
    {
        SupervisorTurnService.SanitizeAskPayloadJson("""{"question":"which db should I use?"}""")
            .ShouldBeNull("null = unchanged — the caller keeps the original bytes and idempotency key");
    }

    [Fact]
    public void A_minted_payload_is_rewritten_question_only_with_every_sibling_key_preserved_verbatim()
    {
        var payload = JsonSerializer.Serialize(new
        {
            question = $"{SupervisorDeliveryGate.QuestionPrefix}OK to finish?",
            rationale = new { why = "the model explains itself", evidence = "turn 3" },
        }, AgentJson.Options);

        var rewritten = SupervisorTurnService.SanitizeAskPayloadJson(payload);

        rewritten.ShouldNotBeNull();
        var root = JsonDocument.Parse(rewritten!).RootElement;
        root.GetProperty("question").GetString().ShouldBe("OK to finish?");
        root.GetProperty("rationale").GetProperty("why").GetString().ShouldBe("the model explains itself", "a typed-DTO rebuild would drop the rationale — the rewrite must be surgical");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("""{"question":null}""")]
    [InlineData("{}")]
    public void A_malformed_or_questionless_payload_is_left_untouched(string payload)
    {
        SupervisorTurnService.SanitizeAskPayloadJson(payload).ShouldBeNull();
    }

    // ── Server-authored exemption (the sweep's blocker): genuine server cards keep their identity ──
    //
    // The critic decorator returns its GENUINE escalation card straight out of the decider pipeline — THROUGH the
    // clamp slot. A position-based exemption stripped its marker and disabled the S8 human-absolution loop
    // (approve → re-review → re-escalate forever). The exemption is therefore a FLAG the model cannot set:
    // SupervisorDecision.ServerAuthored, stamped by every server ask constructor, never by the projector.

    [Fact]
    public void Every_server_ask_constructor_stamps_ServerAuthored()
    {
        SupervisorGateEscalation.IntoAskHuman(new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = "{}" },
                new CodeSpace.Messages.Review.CriticVerdict { Mode = CodeSpace.Messages.Enums.ReviewMode.Gate, Approved = false, Rationale = "r" })
            .ServerAuthored.ShouldBeTrue("the escalation card travels THROUGH the clamp — its marker must survive");

        SupervisorPlanConfirmation.IntoAskHuman(planVersion: 1, itemCount: 2, delivery: null, priorApprovedDelivery: null)
            .ServerAuthored.ShouldBeTrue();

        SupervisorApprovalRequest.IntoAskHuman(new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = """{"subtaskIds":["a"]}""" })
            .ServerAuthored.ShouldBeTrue();
    }

    [Fact]
    public void The_projector_can_never_stamp_ServerAuthored_on_a_model_decision()
    {
        SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.AskHuman,
            AskHuman = new SupervisorAskHumanPayload { Question = SupervisorGateEscalation.EscalationMarker },
        }).ServerAuthored.ShouldBeFalse("whatever the MODEL half of the pipeline emits arrives unflagged by construction");
    }
}
