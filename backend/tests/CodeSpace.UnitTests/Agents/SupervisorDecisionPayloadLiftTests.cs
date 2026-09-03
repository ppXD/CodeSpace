using System.Text.Json;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: <see cref="SupervisorDecisionPayloadLift"/> — the deterministic repair of a decision whose payload fields the
/// model wrote at the ROOT instead of inside its kind's sub-object. The fixtures marked LIVE are verbatim raw replies
/// captured from the supervisor eval run of 2026-08-19, which produced 68 of these in one pass (46 spawn, 21 retry, 1
/// stop) — each one previously costing a model round-trip to recover information the first reply already carried.
///
/// Pins the lift's conservatism as hard as its capability: it moves only declared fields of the target sub-object, never
/// overwrites what the model did nest, never invents a payload that was genuinely absent, and declines (null) rather than
/// guessing — because a lift that guesses would manufacture a decision the model never authored, which is worse than the
/// extra round-trip it saves. Also pins the derivation against the schema, so a kind added later is covered without
/// touching the lift.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDecisionPayloadLiftTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    private static SupervisorModelDecision Bind(JsonElement element) =>
        JsonSerializer.Deserialize<SupervisorModelDecision>(element.GetRawText(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

    [Fact]
    public void A_live_flattened_retry_becomes_coherent_without_a_model_round_trip()
    {
        // LIVE fixture — verbatim from the 2026-08-19 eval run.
        var raw = Json("""
            {"kind":"retry","rationale":{"why":"Subtask s2 failed with a build error (missing symbol), so it cannot be merged.","evidence":"agent 1: Failed — error: build failed: missing symbol referenced by s2"},"subtaskId":"s2","revisedInstruction":"Return HTTP 400 for a malformed email."}
            """);

        SupervisorDecisionCoherence.MissingPayload(Bind(raw)).ShouldNotBeNull("the fixture is the defective shape this exists to repair");

        var lifted = SupervisorDecisionPayloadLift.Lift(raw, SupervisorDecisionKinds.Retry).ShouldNotBeNull();
        var model = Bind(lifted);

        SupervisorDecisionCoherence.MissingPayload(model).ShouldBeNull("the fields were all present, only mis-nested — so no second model call is needed");
        model.Retry!.SubtaskId.ShouldBe("s2");
        model.Rationale!.Why.ShouldNotBeNullOrWhiteSpace("the root-level rationale is a root property and must NOT be swept into the payload");
        lifted.TryGetProperty("subtaskId", out _).ShouldBeFalse("a moved field is removed from the root, not duplicated");
    }

    [Fact]
    public void A_live_flattened_spawn_becomes_coherent_without_a_model_round_trip()
    {
        // LIVE fixture — verbatim from the 2026-08-19 eval run. Spawn was the most frequent case, 46 of the 68.
        var raw = Json("""
            {"kind":"spawn","subtaskIds":["investigate"],"rationale":{"why":"Only 'investigate' is ready to spawn now.","evidence":"Dependency frontier shows 'investigate' ready."}}
            """);

        SupervisorDecisionCoherence.MissingPayload(Bind(raw)).ShouldNotBeNull();

        var model = Bind(SupervisorDecisionPayloadLift.Lift(raw, SupervisorDecisionKinds.Spawn).ShouldNotBeNull());

        SupervisorDecisionCoherence.MissingPayload(model).ShouldBeNull();
        model.Spawn!.SubtaskIds.ShouldBe(new[] { "investigate" });
    }

    [Fact]
    public void A_genuinely_absent_payload_is_declined_rather_than_invented()
    {
        // The case the model repair must still own: the reply carries NO payload field anywhere, so there is nothing to
        // move. Inventing one here would hand the executor a decision the model never authored.
        var raw = Json("""{"kind":"retry","rationale":{"why":"retrying","evidence":"it failed"}}""");

        SupervisorDecisionPayloadLift.Lift(raw, SupervisorDecisionKinds.Retry).ShouldBeNull("no payload field is present to lift — this reply needs the model, not a move");
    }

    [Fact]
    public void A_half_flattened_reply_keeps_what_the_model_actually_nested()
    {
        // Both a nested sub-object AND a stray root copy. The nested value is the model's own authored placement, so it wins.
        var raw = Json("""{"kind":"retry","retry":{"subtaskId":"nested"},"subtaskId":"stray","revisedInstruction":"do better"}""");

        var lifted = SupervisorDecisionPayloadLift.Lift(raw, SupervisorDecisionKinds.Retry).ShouldNotBeNull();
        var model = Bind(lifted);

        model.Retry!.SubtaskId.ShouldBe("nested", "a value the model nested is never overwritten by a root duplicate");
        model.Retry!.RevisedInstruction.ShouldBe("do better", "the field it did NOT nest is still lifted in");
    }

    [Fact]
    public void A_root_property_that_belongs_at_the_root_is_never_moved()
    {
        // `rationale` is declared at the root for every verb. If any payload ever declares a field of the same name, the
        // root copy must stay put — otherwise the trace loses the reasoning it exists to carry.
        var raw = Json("""{"kind":"spawn","rationale":{"why":"w","evidence":"e"},"subtaskIds":["a"]}""");

        var lifted = SupervisorDecisionPayloadLift.Lift(raw, SupervisorDecisionKinds.Spawn).ShouldNotBeNull();

        lifted.TryGetProperty("rationale", out var rationale).ShouldBeTrue("the root rationale survives the lift");
        rationale.GetProperty("why").GetString().ShouldBe("w");
    }

    [Theory]
    [InlineData("resolve")]           // carries no sub-object at all by design — the server assembles the task
    [InlineData("not_a_verb")]        // an unknown kind names no sub-object
    [InlineData("")]                  // an empty kind names nothing
    public void A_kind_the_schema_declares_no_payload_for_is_declined(string kind)
    {
        SupervisorDecisionPayloadLift.Lift(Json("""{"kind":"resolve","subtaskId":"s1"}"""), kind).ShouldBeNull();
    }

    [Fact]
    public void A_non_object_reply_is_declined_rather_than_throwing()
    {
        SupervisorDecisionPayloadLift.Lift(Json("""["kind","retry"]"""), SupervisorDecisionKinds.Retry).ShouldBeNull();
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan, "plan")]
    [InlineData(SupervisorDecisionKinds.Spawn, "spawn")]
    [InlineData(SupervisorDecisionKinds.Retry, "retry")]
    [InlineData(SupervisorDecisionKinds.AskHuman, "askHuman")]
    [InlineData(SupervisorDecisionKinds.Stop, "stop")]
    [InlineData(SupervisorDecisionKinds.AmendAcceptance, "amendAcceptance")]
    public void Every_kind_that_coherence_demands_a_payload_for_resolves_to_that_sub_object(string kind, string expected)
    {
        // The DRIFT DETECTOR. The lift derives kind→sub-object from the schema by camelCasing the verb; this fails the day
        // a kind ships a payload whose property name does not follow that convention, which is exactly when a silent
        // decline would start costing a round-trip again with nothing pointing at why.
        SupervisorDecisionPayloadLift.PayloadPropertyFor(kind).ShouldBe(expected);
        SupervisorDecisionPayloadLift.FieldsOf(kind).ShouldNotBeEmpty($"kind '{kind}' must expose the field names the lift moves");
    }

    [Fact]
    public void The_fields_it_will_move_come_from_the_schema_not_a_local_table()
    {
        // If these were hardcoded here they would drift from the wire the model is actually given. Spot-check the two
        // highest-volume kinds against the schema's own declarations.
        SupervisorDecisionPayloadLift.FieldsOf(SupervisorDecisionKinds.Retry).ShouldContain("subtaskId");
        SupervisorDecisionPayloadLift.FieldsOf(SupervisorDecisionKinds.Spawn).ShouldContain("subtaskIds");
        SupervisorDecisionPayloadLift.FieldsOf(SupervisorDecisionKinds.Plan).ShouldContain("subtasks");
    }

    // ─── The rationale-sourced stop narration floor ─────────────────────────────

    [Fact]
    public void A_live_stop_that_wrote_only_its_rationale_recovers_the_summary_the_model_did_author()
    {
        // LIVE fixture — verbatim from real-model run 33755336097 ("live-brain whole-loop — supervisor arcs",
        // 2026-09-03T14:04:12Z). The model chose 'stop' and wrote NOTHING but 'kind' + 'rationale': there is no
        // 'stop' object and no stop field at the root, so the generic root→sub-object lift has nothing to move.
        var raw = Json("""
            {"kind":"stop","rationale":{"why":"Both subtasks are done and accepted, but the integration remains CONFLICTED and the resolver's reconciliation was NOT verified. The resolve cap is spent, so the only honest path is to stop and leave the conflict for a human.","evidence":"Resolve outcome: 'resolution NOT verified'. Merge outcome: 'INTEGRATION CONFLICTED'."}}
            """);

        SupervisorDecisionPayloadLift.Lift(raw, SupervisorDecisionKinds.Stop).ShouldBeNull("the generic lift declines — there is no root field to move");

        var narrated = SupervisorDecisionPayloadLift.LiftStopNarration(raw).ShouldNotBeNull();
        var model = Bind(narrated);

        SupervisorDecisionCoherence.MissingPayload(model).ShouldBeNull("a stop whose narration was recovered is executable");
        model.Stop!.Summary.ShouldContain("the only honest path is to stop", customMessage: "the summary is the model's OWN words, never a manufactured sentence");
        model.Stop!.Summary.ShouldContain("INTEGRATION CONFLICTED", customMessage: "the evidence the model cited rides along, so the terminal card explains itself");
        model.Rationale!.Why.ShouldNotBeNullOrWhiteSpace("the root rationale is copied, never moved out of the trace");
    }

    [Fact]
    public void A_stop_object_present_but_summary_blank_is_narrated_without_touching_the_outcome()
    {
        // The sibling shape: coherence's sub-object test passes, but SupervisorPublishGate rejects a published run's
        // summary-less stop and substitutes an ask_human — the same park, one layer later.
        var raw = Json("""{"kind":"stop","stop":{"outcome":"failed"},"rationale":{"why":"The build never went green.","evidence":"3 attempts, identical error."}}""");

        var model = Bind(SupervisorDecisionPayloadLift.LiftStopNarration(raw).ShouldNotBeNull());

        model.Stop!.Summary.ShouldContain("The build never went green.");
        model.Stop!.Outcome.ShouldBe("failed", "the terminal label the model DID author is never overwritten — the repair restores words, it never changes the claim");
    }

    [Fact]
    public void A_stop_that_already_carries_its_summary_is_left_alone()
    {
        var raw = Json("""{"kind":"stop","stop":{"outcome":"completed","summary":"Shipped the validator."},"rationale":{"why":"w","evidence":"e"}}""");

        SupervisorDecisionPayloadLift.LiftStopNarration(raw).ShouldBeNull("nothing to recover — a no-op declines rather than rewriting an authored payload");
    }

    [Fact]
    public void A_stop_with_no_rationale_prose_is_declined_rather_than_invented()
    {
        // The floor recovers WORDS THE MODEL WROTE. With no rationale there are none, and a manufactured summary
        // would be the server claiming an ending on the model's behalf.
        SupervisorDecisionPayloadLift.LiftStopNarration(Json("""{"kind":"stop"}""")).ShouldBeNull();
        SupervisorDecisionPayloadLift.LiftStopNarration(Json("""{"kind":"stop","rationale":{"why":"   "}}""")).ShouldBeNull();
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    [InlineData(SupervisorDecisionKinds.AmendAcceptance)]
    [InlineData(SupervisorDecisionKinds.Plan)]
    public void No_other_kind_is_ever_narrated_from_its_rationale(string kind)
    {
        // WHY STOP ALONE: every other verb's payload names ENTITIES the run must act on — a subtask id, a question,
        // a replacement oracle. Prose cannot yield those, and a guess would fan out work the model never chose.
        // 'stop' commands nothing; its payload only DESCRIBES the ending, which the rationale already does.
        var raw = Json($$"""{"kind":"{{kind}}","rationale":{"why":"w","evidence":"e"} }""");

        SupervisorDecisionPayloadLift.LiftStopNarration(raw).ShouldBeNull();
    }

    [Fact]
    public void The_narrated_outcome_matches_the_projector_own_substitute_for_an_absent_stop()
    {
        // DRIFT DETECTOR. The floor must never make the terminal claim any MORE than the projector already makes for
        // the same shape: today an absent stop projects with the substitute label read below. If that substitute
        // changes, this floor has silently started claiming something different — fail here, not in production.
        var narrated = Bind(SupervisorDecisionPayloadLift.LiftStopNarration(Json("""{"kind":"stop","rationale":{"why":"done"}}""")).ShouldNotBeNull());
        var substituted = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Stop });

        narrated.Stop!.Outcome.ShouldBe(JsonDocument.Parse(substituted.PayloadJson).RootElement.GetProperty("outcome").GetString());
    }
}
