using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the pure Tier-0 plan validator (<see cref="SupervisorPlanValidator"/>) — fail-fast on a structurally
/// invalid DependsOn DAG before the dependency gate would stall on it. Pins: a flat plan and a well-formed DAG (chain /
/// diamond) pass; a dangling reference (a dep on an undeclared subtask), a self-loop, and a cycle (2-node and longer)
/// force a <see cref="SupervisorStopReasons.PlanInvalid"/> stop; a non-plan decision and a malformed payload are ignored.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorPlanValidatorTests
{
    [Fact]
    public void A_flat_plan_is_valid()
    {
        SupervisorPlanValidator.Validate(Plan(("a", null), ("b", null))).ShouldBeNull("no DependsOn edges → nothing to validate (byte-identical to pre-slice)");
    }

    [Fact]
    public void A_well_formed_chain_is_valid()
    {
        SupervisorPlanValidator.Validate(Plan(("a", null), ("b", new[] { "a" }), ("c", new[] { "b" }))).ShouldBeNull();
    }

    [Fact]
    public void A_diamond_is_valid()
    {
        // d depends on b and c, both of which depend on a — a well-formed DAG with a join, no cycle.
        SupervisorPlanValidator.Validate(Plan(("a", null), ("b", new[] { "a" }), ("c", new[] { "a" }), ("d", new[] { "b", "c" }))).ShouldBeNull();
    }

    [Fact]
    public void A_dangling_dependency_reference_is_rejected()
    {
        SupervisorPlanValidator.Validate(Plan(("a", null), ("b", new[] { "x" })))
            .ShouldBe(SupervisorStopReasons.PlanInvalid, "b depends on an undeclared 'x' — the gate could never satisfy it");
    }

    [Fact]
    public void A_self_loop_is_rejected()
    {
        SupervisorPlanValidator.Validate(Plan(("a", new[] { "a" }))).ShouldBe(SupervisorStopReasons.PlanInvalid);
    }

    [Fact]
    public void A_two_node_cycle_is_rejected()
    {
        SupervisorPlanValidator.Validate(Plan(("a", new[] { "b" }), ("b", new[] { "a" }))).ShouldBe(SupervisorStopReasons.PlanInvalid);
    }

    [Fact]
    public void A_longer_cycle_is_rejected()
    {
        SupervisorPlanValidator.Validate(Plan(("a", new[] { "c" }), ("b", new[] { "a" }), ("c", new[] { "b" })))
            .ShouldBe(SupervisorStopReasons.PlanInvalid, "a→c→b→a is a cycle");
    }

    [Fact]
    public void A_cycle_in_a_plan_that_also_has_an_acyclic_branch_is_rejected()
    {
        // a valid chain (x→y) alongside a cycle (a↔b) — the cycle still trips.
        SupervisorPlanValidator.Validate(Plan(("x", null), ("y", new[] { "x" }), ("a", new[] { "b" }), ("b", new[] { "a" })))
            .ShouldBe(SupervisorStopReasons.PlanInvalid);
    }

    [Fact]
    public void A_non_plan_decision_is_ignored()
    {
        SupervisorPlanValidator.Validate(new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = """{"subtaskIds":["a"]}""" }).ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{")]
    public void A_malformed_payload_passes_defensively(string payload)
    {
        SupervisorPlanValidator.Validate(new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = payload })
            .ShouldBeNull("the canonical payload always parses; a parse failure is a deeper bug, not a plan-shape error to reject here");
    }

    private static SupervisorDecision Plan(params (string Id, string[]? DependsOn)[] subtasks)
    {
        var payload = JsonSerializer.Serialize(new
        {
            goal = "g",
            subtasks = subtasks.Select(s => s.DependsOn is null
                ? (object)new { id = s.Id, title = s.Id, instruction = "do" }
                : new { id = s.Id, title = s.Id, instruction = "do", dependsOn = s.DependsOn }),
        }, AgentJson.Options);

        return new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = payload };
    }
}
