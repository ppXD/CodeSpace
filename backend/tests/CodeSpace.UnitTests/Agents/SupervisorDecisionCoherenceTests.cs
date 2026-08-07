using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: <see cref="SupervisorDecisionCoherence"/> — the pre-projection check for the ONE invariant the
/// decision schema cannot express (a per-kind conditional <c>required</c>): the chosen kind's payload sub-object
/// must be present. Pins each defective shape's named defect, each coherent/exempt shape's null, and — the
/// drift detector — that the set of kinds this class demands a payload for is derived from
/// <see cref="SupervisorDecisionSchema.ResponseSchema"/>'s own per-verb <c>required</c> declarations, so the
/// schema and the coherence check cannot disagree silently.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDecisionCoherenceTests
{
    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan, "plan")]
    [InlineData(SupervisorDecisionKinds.Spawn, "spawn")]
    [InlineData(SupervisorDecisionKinds.Retry, "retry")]
    [InlineData(SupervisorDecisionKinds.AskHuman, "askHuman")]
    [InlineData(SupervisorDecisionKinds.Stop, "stop")]
    public void A_kind_whose_payload_sub_object_is_missing_is_named_incoherent(string kind, string property)
    {
        var defect = SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = kind });

        defect.ShouldNotBeNull($"kind '{kind}' without its payload is unexecutable and must be repairable before projection substitutes an empty payload");
        defect.ShouldContain($"'{property}'", customMessage: "the defect names the exact sub-object the payload must ride in, so the repair prompt tells the model where to nest it");
        defect.ShouldContain("anywhere else", Case.Insensitive, "the defect warns against the live-observed flattening (fields at the top level are never read)");
    }

    [Fact]
    public void A_spawn_with_an_EMPTY_subtaskIds_array_is_named_incoherent()
    {
        // The one shape that slips every earlier net: the sub-object is present (no bind error), minItems is not
        // validated client-side, and the projector passes it through — the executor then rejects it a full turn
        // later. At decide time the dependency clamp has not run yet, so an empty spawn is always the model's own
        // authorship, never a server-emptied fan-out.
        var model = new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Spawn, Spawn = new SupervisorSpawnPayload() };

        SupervisorDecisionCoherence.MissingPayload(model).ShouldNotBeNull().ShouldContain("EMPTY");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_retry_with_a_blank_subtaskId_is_named_incoherent(string blank)
    {
        var model = new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Retry, Retry = new SupervisorRetryPayload { SubtaskId = blank } };

        SupervisorDecisionCoherence.MissingPayload(model).ShouldNotBeNull().ShouldContain("BLANK");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_ask_human_with_a_blank_question_is_named_incoherent(string blank)
    {
        var model = new SupervisorModelDecision { Kind = SupervisorDecisionKinds.AskHuman, AskHuman = new SupervisorAskHumanPayload { Question = blank } };

        SupervisorDecisionCoherence.MissingPayload(model).ShouldNotBeNull().ShouldContain("BLANK");
    }

    [Fact]
    public void A_decision_carrying_the_payload_its_kind_names_is_coherent()
    {
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = new SupervisorPlanPayload() }).ShouldBeNull();
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Spawn, Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "st-1" } } }).ShouldBeNull();
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Retry, Retry = new SupervisorRetryPayload { SubtaskId = "st-1" } }).ShouldBeNull();
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.AskHuman, AskHuman = new SupervisorAskHumanPayload { Question = "which db?" } }).ShouldBeNull();
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Stop, Stop = new SupervisorStopPayload { Outcome = "completed", Summary = "done" } }).ShouldBeNull();
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Merge)]     // schema: required [] — an empty merge legitimately means "merge everything mergeable"
    [InlineData(SupervisorDecisionKinds.Resolve)]   // schema: no payload sub-object at all
    [InlineData("")]                                // blank kind — the bind-check flow owns it, never this
    [InlineData("wat")]                             // unknown kind — the projector fail-closes it to a stop, never this
    public void An_exempt_kind_is_never_repaired(string kind)
    {
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = kind }).ShouldBeNull();
    }

    [Fact]
    public void The_demanded_kind_set_is_the_schema_own_required_declarations()
    {
        // THE drift detector: derive, from the schema itself, which kinds declare a payload sub-object with at
        // least one required field — exactly those (and only those) must be demanded by the coherence check. A
        // schema edit (a new verb, a verb's payload going fully-optional) that is not mirrored here fails THIS
        // test, never silently diverges (the conformance-matrix lesson: assert per cell, not in aggregate).
        var properties = SupervisorDecisionSchema.ResponseSchema.GetProperty("properties");

        foreach (var kindElement in properties.GetProperty("kind").GetProperty("enum").EnumerateArray())
        {
            var kind = kindElement.GetString()!;
            var property = kind == SupervisorDecisionKinds.AskHuman ? "askHuman" : kind;

            var schemaDemandsPayload = properties.TryGetProperty(property, out var sub) && sub.TryGetProperty("required", out var required) && required.GetArrayLength() > 0;
            var coherenceDemandsPayload = SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = kind }) is not null;

            coherenceDemandsPayload.ShouldBe(schemaDemandsPayload, $"kind '{kind}': the schema {(schemaDemandsPayload ? "declares required payload fields" : "declares no required payload field")}, so the coherence check must {(schemaDemandsPayload ? "demand" : "not demand")} the '{property}' sub-object");
        }
    }
}
