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
}
