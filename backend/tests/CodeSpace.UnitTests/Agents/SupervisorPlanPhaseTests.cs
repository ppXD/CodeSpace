using System.Text.Json;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins C1 of the L4 arc — model-authored semantic phases (<see cref="SupervisorPlanPhase"/>) carried
/// optionally on the <c>plan</c> payload. A plan WITHOUT phases must serialize to the exact pre-field bytes (the
/// idempotency-key input), so the optional <c>phases[]</c> (and a phase's optional acceptance) is
/// <c>[JsonIgnore(WhenWritingNull)]</c>, the schema keeps <c>required:["goal","subtasks"]</c>, and the projector is
/// unchanged. Pins that PLUS the end-to-end model-authoring path (a schema-shaped plan JSON binds into the decision).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorPlanPhaseTests
{
    private static SupervisorPlanPayload FlatPlan() => new()
    {
        Goal = "g",
        Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "T", Instruction = "do" } },
    };

    [Fact]
    public void A_plan_without_phases_projects_byte_identical_to_before()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = FlatPlan() });

        decision.PayloadJson.ShouldBe("""{"goal":"g","subtasks":[{"id":"s1","title":"T","instruction":"do"}]}""",
            "a plan with no phases must serialize to the pre-field bytes — the idempotency key depends on it");
    }

    [Fact]
    public void A_plan_with_phases_appends_only_the_phases_array()
    {
        var plan = FlatPlan() with { Phases = new[] { new SupervisorPlanPhase { Id = "p1", Title = "Investigate", SubtaskIds = new[] { "s1" } } } };

        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = plan });

        decision.PayloadJson.ShouldBe("""{"goal":"g","subtasks":[{"id":"s1","title":"T","instruction":"do"}],"phases":[{"id":"p1","title":"Investigate","subtaskIds":["s1"]}]}""",
            "phases is appended after subtasks; a phase's null acceptance is omitted");
    }

    [Fact]
    public void A_phase_with_acceptance_round_trips()
    {
        var plan = FlatPlan() with
        {
            Phases = new[]
            {
                new SupervisorPlanPhase { Id = "p1", Title = "Verify", SubtaskIds = new[] { "s1", "s2" }, Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "npm", "test" }, Description = "suite green" } },
            },
        };

        var back = JsonSerializer.Deserialize<SupervisorPlanPayload>(JsonSerializer.Serialize(plan, CodeSpace.Core.Services.Agents.AgentJson.Options), CodeSpace.Core.Services.Agents.AgentJson.Options)!;

        var phase = back.Phases!.Single();
        phase.Id.ShouldBe("p1");
        phase.Title.ShouldBe("Verify");
        phase.SubtaskIds.ShouldBe(new[] { "s1", "s2" });
        phase.Acceptance.ShouldNotBeNull();
        phase.Acceptance!.Command.ShouldBe(new[] { "npm", "test" });
        phase.Acceptance.Description.ShouldBe("suite green");
    }

    [Fact]
    public void A_model_emitted_plan_with_phases_binds_through_the_schema_options()
    {
        const string modelJson = """
            { "kind": "plan", "plan": { "goal": "ship", "subtasks": [{"id":"s1","title":"T","instruction":"do"}],
              "phases": [
                { "id": "p1", "title": "Implement", "subtaskIds": ["s1"], "acceptance": { "command": ["make","test"], "description": "builds + tests" } },
                { "id": "p2", "title": "Review" }
              ] } }
            """;

        var model = JsonSerializer.Deserialize<SupervisorModelDecision>(modelJson, SupervisorDecisionSchema.Options)!;

        model.Plan!.Phases.ShouldNotBeNull();
        model.Plan.Phases!.Count.ShouldBe(2);
        var p1 = model.Plan.Phases[0];
        p1.Id.ShouldBe("p1");
        p1.Title.ShouldBe("Implement");
        p1.SubtaskIds.ShouldBe(new[] { "s1" });
        p1.Acceptance!.Command.ShouldBe(new[] { "make", "test" });
        model.Plan.Phases[1].Title.ShouldBe("Review");
        model.Plan.Phases[1].Acceptance.ShouldBeNull("a phase without acceptance carries none");
    }

    [Fact]
    public void A_model_emitted_plan_without_phases_leaves_it_null()
    {
        var model = JsonSerializer.Deserialize<SupervisorModelDecision>(
            """{ "kind": "plan", "plan": { "goal": "g", "subtasks": [{"id":"s1","title":"T","instruction":"do"}] } }""", SupervisorDecisionSchema.Options)!;

        model.Plan!.Phases.ShouldBeNull();
    }

    // ── Schema contract: phases[] is an optional, closed array; plan still requires goal + subtasks ──

    [Fact]
    public void The_plan_block_still_requires_only_goal_and_subtasks()
    {
        PlanBlock().GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "goal", "subtasks" }, ignoreOrder: true, "phases must stay OPTIONAL — a flat plan validates verbatim");
    }

    [Fact]
    public void The_phases_array_is_closed_and_requires_id_and_title()
    {
        var item = PlanBlock().GetProperty("properties").GetProperty("phases").GetProperty("items");

        item.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse("the phase item rejects invented fields");
        item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "id", "title" }, ignoreOrder: true);
        item.GetProperty("properties").GetProperty("acceptance").GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldContain("command", "a phase's acceptance reuses the command-required acceptance shape");
    }

    private static JsonElement PlanBlock() => SupervisorDecisionSchema.ResponseSchema.GetProperty("properties").GetProperty("plan");
}
