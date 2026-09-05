using System.Text.Json;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit (C4): the two word-matched judgements this PR replaces with structured fields.
///
/// <para>ONE — a stop's terminal verdict. <c>stop.outcome</c> is now a CLOSED schema enum
/// (<c>completed | gave_up | needs_clarification</c>) and REQUIRED. The legacy success words survive only as a
/// READ path for old tapes; a NEW decision authoring one is repaired to the enum by
/// <see cref="SupervisorDecisionPayloadLift.NormalizeStopOutcome"/>, and anything the server cannot read
/// fail-closes to <c>gave_up</c> rather than being promoted into a success claim.</para>
///
/// <para>TWO — a human card's approval. The verdict is now the <c>decision</c> field the answering surface sends;
/// the <c>approve</c>-prefix read remains ONLY as a fallback for an answer carrying no such field, which is why a
/// 繁中「批准」typed with no decision field still reads as feedback (documented below — the fix is a surface that
/// sends the field, not a wider word list).</para>
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorStructuredOutcomeTests
{
    // ── The closed stop enum ───────────────────────────────────────────────

    [Fact]
    public void The_schema_pins_stop_outcome_to_the_closed_enum_and_requires_it()
    {
        var stop = SupervisorDecisionSchema.ResponseSchema.GetProperty("properties").GetProperty("stop");

        stop.GetProperty("properties").GetProperty("outcome").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!)
            .ShouldBe(SupervisorStopPayload.ConformantOutcomes, "the schema's enum IS the payload's conformant set — a drift lets the model author a label the server then repairs away");

        stop.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "outcome", "summary" });
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("gave_up", true)]
    [InlineData("needs_clarification", true)]
    [InlineData("done", false)]        // a LEGACY word — readable on an old tape, never conformant for a new decision
    [InlineData("ok", false)]
    [InlineData("Completed", false)]   // exact: the enum is not case-tolerant
    [InlineData("完成", false)]
    [InlineData("failed", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_exact_enum_members_are_conformant(string? outcome, bool conformant) =>
        SupervisorStopPayload.IsConformantOutcome(outcome).ShouldBe(conformant);

    [Theory]
    [InlineData("done", "completed")]        // a legacy success word recovers the model's own meaning
    [InlineData("ok", "completed")]
    [InlineData("succeeded", "completed")]
    [InlineData("needs-clarification", "needs_clarification")]
    [InlineData("failed", "gave_up")]        // honest non-success — fail-closed, meaning preserved
    [InlineData("abandoned", "gave_up")]
    [InlineData("完成", "gave_up")]           // a label the server cannot read is NEVER promoted into a success
    public void A_non_conformant_outcome_normalizes_onto_the_enum(string authored, string expected) =>
        SupervisorStopPayload.NormalizeOutcome(authored).ShouldBe(expected);

    [Theory]
    [InlineData("completed")]
    [InlineData("gave_up")]
    [InlineData("needs_clarification")]
    public void A_conformant_outcome_needs_no_repair(string outcome) =>
        SupervisorStopPayload.NormalizeOutcome(outcome).ShouldBeNull();

    [Fact]
    public void A_new_decision_authoring_a_legacy_word_is_repaired_not_accepted()
    {
        var repaired = SupervisorDecisionPayloadLift.NormalizeStopOutcome(Decision("""{"kind":"stop","stop":{"outcome":"done","summary":"shipped it"}}"""));

        repaired.ShouldNotBeNull("a live model writing 'done' must not terminalize with a label outside the enum");

        var stop = repaired!.Value.GetProperty("stop");
        stop.GetProperty("outcome").GetString().ShouldBe(SupervisorStopPayload.CompletedOutcome);
        stop.GetProperty("summary").GetString().ShouldBe("shipped it", "the repair touches the LABEL only — the model's words are kept verbatim");
        stop.GetProperty("outcomeRepairedFrom").GetString().ShouldBe("done", "the journal must show what the model actually wrote");
    }

    [Fact]
    public void A_new_decision_authoring_an_unreadable_label_fail_closes_to_give_up() =>
        SupervisorDecisionPayloadLift.NormalizeStopOutcome(Decision("""{"kind":"stop","stop":{"outcome":"完成","summary":"做完了"}}"""))!
            .Value.GetProperty("stop").GetProperty("outcome").GetString()
            .ShouldBe(SupervisorStopPayload.GaveUpOutcome, "an unreadable success claim fail-closes — the enum is how a model says 'completed'");

    [Fact]
    public void A_conformant_stop_is_left_untouched() =>
        SupervisorDecisionPayloadLift.NormalizeStopOutcome(Decision("""{"kind":"stop","stop":{"outcome":"completed","summary":"done"}}"""))
            .ShouldBeNull("no repair, so the decision bytes — and its idempotency key — are unchanged");

    [Fact]
    public void An_outcome_less_stop_is_left_to_the_narration_lift() =>
        SupervisorDecisionPayloadLift.NormalizeStopOutcome(Decision("""{"kind":"stop","stop":{"summary":"only words"}}"""))
            .ShouldBeNull("this repair never AUTHORS an outcome — LiftStopNarration owns the absent-label fill");

    [Fact]
    public void A_non_stop_decision_is_never_touched() =>
        SupervisorDecisionPayloadLift.NormalizeStopOutcome(Decision("""{"kind":"merge","merge":{}}""")).ShouldBeNull();

    [Fact]
    public void The_narration_lifts_assumed_label_is_an_enum_member() =>
        SupervisorStopPayload.IsConformantOutcome(SupervisorDecisionPayloadLift.AssumedGiveUpOutcome)
            .ShouldBeTrue("#1755's fail-closed fill must itself be a value the closed enum admits");

    [Theory]
    [InlineData("done")]
    [InlineData("ok")]
    [InlineData("succeeded")]
    public void Legacy_success_words_still_read_as_success_on_an_OLD_tape(string outcome) =>
        SupervisorStopPayload.IsSuccessOutcome(outcome).ShouldBeTrue("a run recorded before the enum existed must keep rendering the way it did — the read path is not where new decisions are policed");

    // ── The structured answer envelope ─────────────────────────────────────

    [Fact]
    public void The_answer_envelope_is_a_closed_three_value_set()
    {
        SupervisorAnswerDecision.All.ShouldBe(new[] { "approve", "revise", "reject" });
        SupervisorAnswerDecision.Field.ShouldBe("decision", "the wire key the FE, the Action wait's values, and the folded outcome all share");
    }

    [Theory]
    [InlineData("approve", true)]
    [InlineData("APPROVE", true)]
    [InlineData(" approve ", true)]
    [InlineData("revise", false)]
    [InlineData("reject", false)]
    [InlineData("approve the rewrite", false)]   // EXACT, never a prefix — no text can widen the field
    [InlineData("批准", false)]
    [InlineData(null, false)]
    public void The_structured_verdict_approves_only_on_an_exact_approve(string? decision, bool approves) =>
        SupervisorAnswerDecision.IsApprove(decision).ShouldBe(approves);

    [Theory]
    [InlineData("""{"answer":"批准","decision":"approve"}""", true)]              // the reported 繁中 case — FIXED by the field
    [InlineData("""{"answer":"looks wrong","decision":"revise"}""", false)]
    [InlineData("""{"answer":"approve — ship it","decision":"revise"}""", false)]  // the FIELD wins; the text is never consulted
    [InlineData("""{"answer":"approve nothing until the tests pass","decision":"approve"}""", true)]
    [InlineData("""{"answer":"approve — ship it"}""", true)]                       // LEGACY fallback: no field → the prefix read
    [InlineData("""{"answer":"revise: not yet"}""", false)]
    [InlineData("""{"answer":"批准"}""", false)]                                   // DOCUMENTED: a legacy client's 繁中 approval still reads as feedback
    [InlineData("""{"answer":null}""", false)]
    [InlineData("""{}""", false)]
    public void The_shared_card_predicate_reads_the_field_first_and_the_text_only_as_a_fallback(string outcomeJson, bool approves) =>
        SupervisorApprovalRequest.OutcomeApproves(outcomeJson).ShouldBe(approves);

    [Fact]
    public void An_unknown_decision_value_is_not_folded_and_falls_back_to_the_text()
    {
        var folded = SupervisorOutcome.FoldAnswerOnto("""{"question":"q","askHumanToken":"tok"}""", "no thanks", decision: "maybe");

        SupervisorOutcome.ReadAskHumanDecision(folded).ShouldBeNull("an unrecognized value is dropped, never coerced into a verdict");
        SupervisorApprovalRequest.OutcomeApproves(folded).ShouldBeFalse();
    }

    [Fact]
    public void Folding_a_decision_preserves_every_other_key_and_stays_idempotent()
    {
        var parked = """{"question":"Approve spawning 2 agent(s)?","askHumanToken":"tok-3","reviews":[{"verdict":"disapprove"}]}""";

        var folded = SupervisorOutcome.FoldAnswerOnto(parked, "批准", SupervisorAnswerDecision.Approve);

        SupervisorOutcome.ReadAskHumanQuestion(folded).ShouldBe("Approve spawning 2 agent(s)?");
        SupervisorOutcome.ReadHumanWaitToken(folded).ShouldBe("tok-3");
        SupervisorOutcome.ReadAskHumanAnswer(folded).ShouldBe("批准");
        SupervisorOutcome.ReadAskHumanDecision(folded).ShouldBe("approve");
        folded.ShouldContain("disapprove", customMessage: "the enrichment folded after the park must survive the answer fold");

        SupervisorOutcome.FoldAnswerOnto(folded, "批准", SupervisorAnswerDecision.Approve).ShouldBe(folded, "re-folding the same answer is byte-identical");
    }

    [Fact]
    public void An_answer_with_no_decision_folds_byte_identically_to_the_pre_C4_shape() =>
        SupervisorOutcome.FoldAnswerOnto("""{"question":"q","askHumanToken":"t"}""", "yes")
            .ShouldNotContain("decision", customMessage: "no field sent → no key written, so no existing tape shifts");

    [Theory]
    [InlineData("""{"action":"answer","by":"u","comment":"批准","values":{"decision":"approve"}}""", "approve")]
    [InlineData("""{"action":"answer","by":"u","comment":"no","values":{"decision":"REVISE"}}""", "revise")]
    [InlineData("""{"action":"answer","by":"u","comment":"no","values":{"decision":"whatever"}}""", null)]
    [InlineData("""{"action":"answer","by":"u","comment":"approve"}""", null)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public void The_wait_payloads_values_carry_the_verdict_to_the_fold(string? payloadJson, string? expected) =>
        SupervisorOutcome.ReadAnswerDecision(payloadJson).ShouldBe(expected);

    // ── The resolver's structured verdict ──────────────────────────────────

    [Fact]
    public void Both_resolver_recipes_ask_for_the_structured_block()
    {
        var single = SupervisorResolverRecipe.BuildInstruction("goal", new SupervisorIntegrationOutcome { Status = "Conflicted" }, new[] { "b1" });
        var multi = SupervisorResolverRecipe.BuildMultiRepoInstruction("goal", new List<ResolverRepoSection>());

        foreach (var instruction in new[] { single, multi })
        {
            instruction.ShouldContain($"```{SupervisorResolverRecipe.VerificationBlock}");
            instruction.ShouldContain(SupervisorResolverRecipe.VerifiedField);
            instruction.ShouldContain(SupervisorResolverRecipe.TestsPassedMarker, customMessage: "the legacy marker is still asked for, so an old reader keeps working");
        }
    }

    [Theory]
    [InlineData("Reconciled both sides.\n```resolution\n{\"verified\": true}\n```", true)]
    [InlineData("```resolution\n{\"verified\": false}\n```\nTests still red.", false)]
    [InlineData("I did not emit RESOLUTION_VERIFIED because the tests failed.", null)]   // no block → the caller falls back
    [InlineData("```resolution\nnot json\n```", null)]
    [InlineData("```resolution\n{\"other\": true}\n```", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_resolvers_verdict_is_a_field_it_set_with_the_marker_as_the_fallback(string? summary, bool? expected) =>
        SupervisorResolverRecipe.ReadVerification(summary).ShouldBe(expected);

    private static JsonElement Decision(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
