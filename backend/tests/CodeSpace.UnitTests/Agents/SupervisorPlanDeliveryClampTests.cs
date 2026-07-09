using System.Text.Json.Nodes;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// DC-1 — pins <see cref="SupervisorTurnService.ClampPlanDelivery"/>: the server clamp of a fresh plan's
/// model-PROPOSED delivery contract against the OPERATOR's own pre-declared one, BEFORE the decision is claimed
/// + frozen. The load-bearing guarantee (mirroring <see cref="SupervisorSpawnClampTests"/>'s own regression): the
/// clamp is a NARROW edit of only the <c>delivery</c> key, so EVERY other root key the projector froze — the
/// decision-level <c>rationale</c>, <c>goal</c>, <c>subtasks</c> — survives verbatim.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorPlanDeliveryClampTests
{
    [Fact]
    public void No_operator_contract_leaves_the_decision_byte_identical()
    {
        var decision = Plan("""{"goal":"g","subtasks":[],"delivery":{"openPullRequest":true,"targetBranch":"develop"}}""");

        var clamped = SupervisorTurnService.ClampPlanDelivery(Context(deliverySpec: null), decision);

        clamped.PayloadJson.ShouldBe(decision.PayloadJson, "no operator contract at all → the model's own proposal passes through UNCHANGED, byte-identical");
    }

    [Fact]
    public void A_non_plan_decision_is_never_touched()
    {
        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Retry, PayloadJson = """{"subtaskId":"s1"}""" };

        var clamped = SupervisorTurnService.ClampPlanDelivery(Context(new DeliverySpec { OpenPullRequest = true }), decision);

        clamped.ShouldBe(decision, "the clamp is scoped to plan decisions only");
    }

    [Fact]
    public void The_operators_declared_false_overrides_a_models_proposed_true_and_keeps_the_rationale()
    {
        // THE regression: a plan with a root rationale, whose delivery gets clamped — the model's "why" must survive.
        var decision = Plan("""{"goal":"g","subtasks":[],"delivery":{"openPullRequest":true,"targetBranch":"main"},"rationale":{"why":"ship it fast","evidence":"the goal asked for a PR"}}""");

        var clamped = SupervisorTurnService.ClampPlanDelivery(Context(new DeliverySpec { OpenPullRequest = false }), decision);

        SupervisorOutcome.ReadPlanDelivery(clamped.PayloadJson)!.OpenPullRequest.ShouldBe(false, "the operator's explicit false overrides the model's proposed true");
        SupervisorOutcome.ReadRationale(clamped.PayloadJson).Why.ShouldBe("ship it fast", "the decision-level rationale survives the delivery clamp");
    }

    [Fact]
    public void An_operator_contract_with_no_model_proposal_is_added_and_keeps_goal_and_subtasks()
    {
        var decision = Plan("""{"goal":"ship the feature","subtasks":[{"id":"s1","title":"T","instruction":"do it"}]}""");

        var clamped = SupervisorTurnService.ClampPlanDelivery(Context(new DeliverySpec { OpenPullRequest = true, TargetBranch = "main" }), decision);

        var delivery = SupervisorOutcome.ReadPlanDelivery(clamped.PayloadJson);
        delivery!.OpenPullRequest.ShouldBe(true);
        delivery.TargetBranch.ShouldBe("main");

        SupervisorOutcome.ReadPlanSubtasks(clamped.PayloadJson).Single().Id.ShouldBe("s1", "goal + subtasks survive the clamp untouched");
    }

    [Fact]
    public void Neither_side_names_anything_removes_the_delivery_key_entirely()
    {
        var decision = Plan("""{"goal":"g","subtasks":[]}""");

        var clamped = SupervisorTurnService.ClampPlanDelivery(Context(new DeliverySpec()), decision);

        JsonNode.Parse(clamped.PayloadJson)!.AsObject().ContainsKey("delivery").ShouldBeFalse("an all-null clamp result omits the key entirely, matching the [JsonIgnore(WhenWritingNull)] shape a typed rebuild would produce");
    }

    [Fact]
    public void A_malformed_model_proposal_still_lets_the_operators_own_contract_apply()
    {
        var decision = Plan("""{"goal":"g","subtasks":[],"delivery":"not-an-object"}""");

        var clamped = SupervisorTurnService.ClampPlanDelivery(Context(new DeliverySpec { OpenPullRequest = true }), decision);

        SupervisorOutcome.ReadPlanDelivery(clamped.PayloadJson)!.OpenPullRequest.ShouldBe(true, "a malformed model proposal degrades to 'none proposed' — the operator's own contract still applies, never a crash");
    }

    [Fact]
    public void The_clamp_is_deterministic()
    {
        var decision = Plan("""{"goal":"g","subtasks":[],"delivery":{"openPullRequest":true}}""");
        var context = Context(new DeliverySpec { TargetBranch = "release" });

        SupervisorTurnService.ClampPlanDelivery(context, decision).PayloadJson
            .ShouldBe(SupervisorTurnService.ClampPlanDelivery(context, decision).PayloadJson, "same input → byte-identical clamped payload (the replay-stable idempotency key)");
    }

    private static SupervisorDecision Plan(string payloadJson) => new() { Kind = SupervisorDecisionKinds.Plan, PayloadJson = payloadJson };

    private static SupervisorTurnContext Context(DeliverySpec? deliverySpec) => new()
    {
        Goal = "ship", SupervisorRunId = Guid.NewGuid(), TeamId = Guid.NewGuid(), NodeId = "sup", TurnNumber = 1,
        DeliverySpec = deliverySpec,
    };
}
