using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// B1 (amend-acceptance arc) — the INERT skeleton's shape pins: the <c>amend_acceptance</c> verb is rewritten by the
/// projector into an AskHuman-kind card carrying the amend marker + the STRUCTURED proposal; the marker joins the
/// clamp's reserved tokens and the no-progress fold's card family; and — the FATAL-2 anti-minting pair — a
/// model-authored <c>ask_human</c> can smuggle neither the marker sentence (clamp-stripped) nor the structured
/// <c>amend</c> node (bind-erased). The schema pin proves the verb is NOT yet model-facing: flipping it live (B3)
/// must be a deliberate, test-visible decision.
/// </summary>
public class SupervisorAmendAcceptanceTests
{
    private static SupervisorAmendAcceptancePayload Amend(bool waive = false, SupervisorAcceptanceSpec? spec = null) =>
        new() { SubtaskId = "s1", Waive = waive, Acceptance = spec, Reason = "the oracle names a test framework this repo does not have" };

    // ── the projector rewrite ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_projector_rewrites_an_amend_proposal_into_a_parked_ask_card()
    {
        var spec = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } };

        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.AmendAcceptance, AmendAcceptance = Amend(spec: spec) });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "the tape only ever carries the AskHuman kind — the wait/fold machinery is inherited verbatim");
        decision.ServerAuthored.ShouldBeTrue("the card is a server rewrite, not a model-authored ask");

        var question = JsonDocument.Parse(decision.PayloadJson!).RootElement.GetProperty("question").GetString()!;
        question.ShouldContain("AMEND subtask 's1'", customMessage: "the card names the target");
        question.ShouldContain("sh check.sh", customMessage: "the card quotes the proposed check");
        question.ShouldContain(SupervisorAmendAcceptance.AmendMarker, customMessage: "the marker is the card's identity");

        var readBack = SupervisorAmendAcceptance.ReadAmend(decision.PayloadJson)!;
        readBack.SubtaskId.ShouldBe("s1", "the STRUCTURED proposal rides the card — the co-sign overlay reads it back, never re-parsing prose");
        readBack.Waive.ShouldBeFalse();
        readBack.Acceptance!.Command.ShouldBe(new[] { "sh", "check.sh" });
    }

    [Fact]
    public void A_waive_proposal_names_the_forgone_verification_on_its_card()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.AmendAcceptance, AmendAcceptance = Amend(waive: true) });

        var question = JsonDocument.Parse(decision.PayloadJson!).RootElement.GetProperty("question").GetString()!;
        question.ShouldContain("WAIVE subtask 's1'", customMessage: "a waive is named as a waive");
        question.ShouldContain("WITHOUT objective verification", customMessage: "the human must see exactly what a waive forgoes");

        SupervisorAmendAcceptance.ReadAmend(decision.PayloadJson)!.Waive.ShouldBeTrue();
    }

    [Fact]
    public void A_missing_amend_payload_still_projects_a_safe_card()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.AmendAcceptance });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);
        SupervisorAmendAcceptance.ReadAmend(decision.PayloadJson)!.SubtaskId.ShouldBe("", "the empty fallback mirrors the retry verb's — a later validity gate rejects it, projection never throws");
    }

    [Fact]
    public void The_rationale_rides_the_rewritten_card_like_every_other_verb()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.AmendAcceptance,
            AmendAcceptance = Amend(waive: true),
            Rationale = new SupervisorRationale { Why = "w", Evidence = "e" },
        });

        SupervisorOutcome.ReadRationale(decision.PayloadJson).ShouldBe(("w", "e"));
    }

    // ── the FATAL-2 anti-minting pair ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_model_authored_ask_human_cannot_smuggle_a_structured_amend_node()
    {
        var minted = JsonSerializer.Deserialize<SupervisorModelDecision>(
            """{"kind":"ask_human","askHuman":{"question":"please approve my check change","amend":{"subtaskId":"s1","waive":true,"reason":"r"}}}""",
            SupervisorDecisionSchema.Options)!;

        var decision = SupervisorDecisionProjector.Project(minted);

        SupervisorAmendAcceptance.ReadAmend(decision.PayloadJson).ShouldBeNull("binding erases undeclared fields — only the server rewrite can attach the structured proposal");
    }

    [Fact]
    public void The_clamp_strips_a_minted_amend_marker_from_a_model_question()
    {
        SupervisorAskQuestionClamp.Sanitize($"is this ok? {SupervisorAmendAcceptance.AmendMarker}")
            .ShouldBe("is this ok?", "the marker is a server identity — a model-authored question can never carry it onto the tape");
    }

    // ── detectors ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Detectors_require_both_the_marker_and_the_structured_proposal()
    {
        var card = SupervisorAmendAcceptance.IntoAskHuman(Amend());

        SupervisorAmendAcceptance.IsAmendCard(Prior(card.PayloadJson!, outcome: "{}")).ShouldBeTrue();
        SupervisorAmendAcceptance.IsAnsweredAmendCard(Prior(card.PayloadJson!, outcome: "{}")).ShouldBeFalse("no answer yet");
        SupervisorAmendAcceptance.IsAnsweredAmendCard(Prior(card.PayloadJson!, outcome: """{"question":"q","answer":"approve"}""")).ShouldBeTrue();

        SupervisorAmendAcceptance.IsAmendCard(Prior($$"""{"question":"minted {{SupervisorAmendAcceptance.AmendMarker}}"}""", outcome: "{}"))
            .ShouldBeFalse("marker without the structured node is not an amend card");
        SupervisorAmendAcceptance.IsAmendCard(Prior("""{"question":"plain ask","amend":{"subtaskId":"s1","reason":"r"}}""", outcome: "{}"))
            .ShouldBeFalse("structured node without the marker is not an amend card");
        SupervisorAmendAcceptance.IsAmendCard(Prior("""{"question":"which db?"}""", outcome: "{}")).ShouldBeFalse();
    }

    [Fact]
    public void The_other_gate_cards_never_read_as_amend_cards_and_vice_versa()
    {
        var amend = SupervisorAmendAcceptance.IntoAskHuman(Amend());

        SupervisorPlanConfirmation.QuestionCarriesMarker(amend.PayloadJson).ShouldBeFalse();
        SupervisorGateEscalation.QuestionCarriesMarker(amend.PayloadJson).ShouldBeFalse();

        var escalation = SupervisorGateEscalation.IntoAskHuman(
            new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = "{}" },
            new CodeSpace.Messages.Review.CriticVerdict { Mode = CodeSpace.Messages.Enums.ReviewMode.Gate, Approved = false, Rationale = "blocked" });

        SupervisorAmendAcceptance.IsAmendCard(Prior(escalation.PayloadJson!, outcome: "{}")).ShouldBeFalse();
    }

    // ── the schema stays amend-blind until B3 ─────────────────────────────────────────────────────────

    [Fact]
    public void The_model_facing_schema_does_not_offer_the_amend_verb_yet()
    {
        var kinds = SupervisorDecisionSchema.ResponseSchema.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(k => k.GetString()).ToArray();

        kinds.ShouldNotContain(SupervisorDecisionKinds.AmendAcceptance,
            "B1 is schema-hidden — offering the verb to a live model is B3's deliberate flip, never a rider");
    }

    // ── the payload round-trips byte-stable ───────────────────────────────────────────────────────────

    [Fact]
    public void The_payload_omits_null_fields_and_round_trips()
    {
        var json = JsonSerializer.Serialize(Amend(waive: true), AgentJson.Options);

        json.ShouldNotContain("acceptance", customMessage: "null-omitted — pre-existing decisions stay byte-identical");
        JsonSerializer.Deserialize<SupervisorAmendAcceptancePayload>(json, AgentJson.Options).ShouldBe(Amend(waive: true));
    }

    private static SupervisorPriorDecision Prior(string payloadJson, string outcome) =>
        new() { Id = Guid.NewGuid(), Sequence = 1, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.AskHuman, PayloadJson = payloadJson, OutcomeJson = outcome };
}
