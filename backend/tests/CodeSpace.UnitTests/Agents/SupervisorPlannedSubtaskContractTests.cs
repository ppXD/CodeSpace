using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins slice 1 of the loopability build-graph arc — the model-authored, VERIFIABLE per-subtask CONTRACT
/// (<see cref="SupervisorPlannedSubtask.DependsOn"/> = the build-graph edges, <see cref="SupervisorPlannedSubtask.Acceptance"/>
/// = the unit's "definition of done") carried optionally on each planned subtask. PURE DATA in this slice: the executor
/// reads nothing new; the fields exist so the model MAY author a DAG + per-unit acceptance the validator (slice 2) and
/// the per-unit gate (slice 3) later consume. Load-bearing guarantee: a subtask WITHOUT a contract serializes to the
/// EXACT pre-field bytes (the idempotency-key input), so both fields are <c>[JsonIgnore(WhenWritingNull)]</c>, the schema
/// keeps <c>required:["id","title","instruction"]</c>, and the projector is unchanged. Mirrors <see cref="SupervisorPlanPhaseTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorPlannedSubtaskContractTests
{
    private static SupervisorPlanPayload FlatPlan() => new()
    {
        Goal = "g",
        Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "T", Instruction = "do" } },
    };

    // ── Byte-identity: the contract is invisible when absent ───────────────────────────

    [Fact]
    public void A_subtask_without_a_contract_projects_byte_identical_to_before()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = FlatPlan() });

        decision.PayloadJson.ShouldBe("""{"goal":"g","subtasks":[{"id":"s1","title":"T","instruction":"do"}]}""",
            "a subtask with no contract must serialize to the pre-field bytes — the idempotency key depends on it");
    }

    [Fact]
    public void A_subtask_with_only_id_title_instruction_omits_every_contract_field()
    {
        var json = JsonSerializer.Serialize(new SupervisorPlannedSubtask { Id = "s1", Title = "T", Instruction = "do" }, AgentJson.Options);

        json.ShouldBe("""{"id":"s1","title":"T","instruction":"do"}""");
        foreach (var field in new[] { "dependsOn", "acceptance" })
            json.ShouldNotContain(field, Case.Insensitive, $"a null {field} must be omitted, not emitted as null (byte-identity)");
    }

    [Fact]
    public void A_subtask_with_a_contract_appends_only_the_contract_fields()
    {
        var plan = FlatPlan() with
        {
            Subtasks = new[]
            {
                new SupervisorPlannedSubtask
                {
                    Id = "s1", Title = "T", Instruction = "do",
                    DependsOn = new[] { "s0" },
                    Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "npm", "test" }, Description = "green" },
                },
            },
        };

        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = plan });

        decision.PayloadJson.ShouldBe("""{"goal":"g","subtasks":[{"id":"s1","title":"T","instruction":"do","dependsOn":["s0"],"acceptance":{"command":["npm","test"],"description":"green"}}]}""",
            "the contract is appended after instruction (dependsOn then acceptance); existing fields are unchanged");
    }

    [Fact]
    public void A_subtask_contract_round_trips_every_field()
    {
        var original = new SupervisorPlannedSubtask
        {
            Id = "s1", Title = "T", Instruction = "do",
            DependsOn = new[] { "s0", "s-prereq" },
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "npm", "test" }, Description = "suite green" },
        };

        var back = JsonSerializer.Deserialize<SupervisorPlannedSubtask>(JsonSerializer.Serialize(original, AgentJson.Options), AgentJson.Options)!;

        back.Id.ShouldBe("s1");
        back.Title.ShouldBe("T");
        back.Instruction.ShouldBe("do");
        back.DependsOn.ShouldBe(new[] { "s0", "s-prereq" });
        back.Acceptance.ShouldNotBeNull();
        back.Acceptance!.Command.ShouldBe(new[] { "npm", "test" });
        back.Acceptance.Description.ShouldBe("suite green");
    }

    // ── End-to-end model authoring: a schema-shaped plan binds the contract ────────────

    [Fact]
    public void A_model_emitted_plan_with_subtask_contracts_binds_through_the_schema_options()
    {
        const string modelJson = """
            { "kind": "plan", "plan": { "goal": "ship", "subtasks": [
                { "id": "s1", "title": "scaffold", "instruction": "scaffold the api" },
                { "id": "s2", "title": "wire", "instruction": "wire the handler", "dependsOn": ["s1"], "acceptance": { "command": ["make","test"], "description": "handler green" } }
            ] } }
            """;

        var model = JsonSerializer.Deserialize<SupervisorModelDecision>(modelJson, SupervisorDecisionSchema.Options)!;

        var s1 = model.Plan!.Subtasks[0];
        s1.DependsOn.ShouldBeNull("a subtask the model authors without dependencies carries none");
        s1.Acceptance.ShouldBeNull("a subtask the model authors without acceptance carries none");

        var s2 = model.Plan.Subtasks[1];
        s2.DependsOn.ShouldBe(new[] { "s1" }, "the dependency edge binds for the DAG validator (slice 2)");
        s2.Acceptance!.Command.ShouldBe(new[] { "make", "test" }, "the per-unit acceptance binds for the spawn-fold gate (slice 3)");
        s2.Acceptance.Description.ShouldBe("handler green");
    }

    [Fact]
    public void A_model_emitted_subtask_without_a_contract_leaves_the_fields_null()
    {
        var model = JsonSerializer.Deserialize<SupervisorModelDecision>(
            """{ "kind": "plan", "plan": { "goal": "g", "subtasks": [{"id":"s1","title":"T","instruction":"do"}] } }""", SupervisorDecisionSchema.Options)!;

        var subtask = model.Plan!.Subtasks.Single();
        subtask.DependsOn.ShouldBeNull();
        subtask.Acceptance.ShouldBeNull();
    }

    // ── Schema contract: the subtask item is an optional-contract, closed object that still requires only id+title+instruction ──

    [Fact]
    public void The_subtask_item_still_requires_only_id_title_instruction()
    {
        SubtaskItem().GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "id", "title", "instruction" }, ignoreOrder: true, "the contract must stay OPTIONAL — a flat subtask validates verbatim (back-compat)");
    }

    [Fact]
    public void The_subtask_item_is_closed_and_exposes_the_optional_contract_fields()
    {
        var item = SubtaskItem();

        item.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse("the subtask item rejects invented fields");

        var props = item.GetProperty("properties");
        props.TryGetProperty("dependsOn", out _).ShouldBeTrue("the subtask exposes its build-graph dependency edges");
        props.GetProperty("acceptance").GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldContain("command", "a subtask's acceptance reuses the command-required acceptance shape");
    }

    private static JsonElement SubtaskItem() => SupervisorDecisionSchema.ResponseSchema
        .GetProperty("properties").GetProperty("plan").GetProperty("properties").GetProperty("subtasks").GetProperty("items");
}
