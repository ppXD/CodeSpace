using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins A1 of the L3→L4 arc — the model-authored OBJECTIVE acceptance noun (<see cref="SupervisorAcceptanceSpec"/>)
/// carried optionally on the <c>stop</c> payload. The load-bearing guarantee is BYTE-IDENTITY: a stop WITHOUT
/// acceptance must serialize to the EXACT bytes it did before this field existed, because those bytes are hashed
/// into the supervisor's server-derived idempotency key — a drift would change the key and break exactly-once
/// replay. The field is therefore <c>[JsonIgnore(WhenWritingNull)]</c> (absent ⇒ omitted), the schema keeps
/// <c>required:["outcome","summary"]</c> (acceptance is optional), and the projector is unchanged. These tests
/// pin all of that PLUS the end-to-end model-authoring path (a schema-shaped JSON binds into the decision).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorAcceptanceSpecTests
{
    // ── Byte-identity: the field must be invisible when absent ────────────────────────

    [Fact]
    public void Stop_without_acceptance_projects_byte_identical_to_before()
    {
        // THE pin: this is the exact canonical PayloadJson a stop produced before the acceptance field existed.
        // The projector path is the real one the turn loop hashes into the idempotency key.
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Stop,
            Stop = new SupervisorStopPayload { Outcome = "completed", Summary = "done" },
        });

        decision.PayloadJson.ShouldBe("""{"outcome":"completed","summary":"done"}""",
            "a stop with no acceptance must serialize to the pre-change bytes — the idempotency key depends on it");
    }

    [Fact]
    public void Stop_with_acceptance_appends_only_the_acceptance_object()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Stop,
            Stop = new SupervisorStopPayload
            {
                Outcome = "completed",
                Summary = "done",
                Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "npm", "test" } },
            },
        });

        decision.PayloadJson.ShouldBe("""{"outcome":"completed","summary":"done","acceptance":{"command":["npm","test"]}}""",
            "acceptance is appended after the existing fields; a null description is omitted");
    }

    // ── The spec itself: command always present, description null-omitted ─────────────

    [Fact]
    public void Acceptance_spec_omits_a_null_description()
    {
        var json = JsonSerializer.Serialize(new SupervisorAcceptanceSpec { Command = new[] { "make", "check" } }, AgentJson.Options);

        json.ShouldBe("""{"command":["make","check"]}""");
        json.ShouldNotContain("description", Case.Insensitive, "a null description must be omitted, not emitted as null");
    }

    [Fact]
    public void Acceptance_spec_emits_a_present_description()
    {
        var json = JsonSerializer.Serialize(
            new SupervisorAcceptanceSpec { Command = new[] { "pytest" }, Description = "all unit tests pass" }, AgentJson.Options);

        json.ShouldBe("""{"command":["pytest"],"description":"all unit tests pass"}""");
    }

    [Fact]
    public void Acceptance_spec_round_trips_through_the_canonical_options()
    {
        var original = new SupervisorAcceptanceSpec { Command = new[] { "go", "test", "./..." }, Description = "suite green" };

        var back = JsonSerializer.Deserialize<SupervisorAcceptanceSpec>(
            JsonSerializer.Serialize(original, AgentJson.Options), AgentJson.Options)!;

        back.Command.ShouldBe(new[] { "go", "test", "./..." });
        back.Description.ShouldBe("suite green");
    }

    // ── Projector determinism (idempotency-key stability) ────────────────────────────

    [Fact]
    public void Projection_is_deterministic_with_acceptance_present()
    {
        var model = new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Stop,
            Stop = new SupervisorStopPayload { Outcome = "completed", Summary = "ok", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "npm", "test" } } },
        };

        SupervisorDecisionProjector.Project(model).PayloadJson
            .ShouldBe(SupervisorDecisionProjector.Project(model).PayloadJson,
                "same model decision → byte-identical canonical payload, with or without acceptance");
    }

    // ── End-to-end model authoring: a schema-shaped JSON binds into the decision ──────

    [Fact]
    public void A_model_emitted_stop_with_acceptance_binds_through_the_schema_options()
    {
        // Exactly the shape the structured-LLM call emits (lower-camel keys), bound via the decider's own options.
        const string modelJson = """
            { "kind": "stop", "stop": { "outcome": "completed", "summary": "shipped", "acceptance": { "command": ["dotnet", "test"], "description": "all green" } } }
            """;

        var model = JsonSerializer.Deserialize<SupervisorModelDecision>(modelJson, SupervisorDecisionSchema.Options)!;

        model.Stop.ShouldNotBeNull();
        model.Stop!.Acceptance.ShouldNotBeNull();
        model.Stop.Acceptance!.Command.ShouldBe(new[] { "dotnet", "test" });
        model.Stop.Acceptance.Description.ShouldBe("all green");
    }

    [Fact]
    public void A_model_emitted_stop_without_acceptance_leaves_it_null()
    {
        var model = JsonSerializer.Deserialize<SupervisorModelDecision>(
            """{ "kind": "stop", "stop": { "outcome": "completed", "summary": "shipped" } }""", SupervisorDecisionSchema.Options)!;

        model.Stop!.Acceptance.ShouldBeNull("a stop the model authors without acceptance carries no spec");
    }

    // ── Schema contract: acceptance is an optional, closed object ─────────────────────

    [Fact]
    public void The_stop_block_still_requires_only_outcome_and_summary()
    {
        var stop = SupervisorDecisionSchema.ResponseSchema.GetProperty("properties").GetProperty("stop");

        stop.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "outcome", "summary" }, ignoreOrder: true,
                "acceptance must stay OPTIONAL — a stop without it validates verbatim (back-compat)");
    }

    [Fact]
    public void The_acceptance_object_is_closed_and_requires_a_command()
    {
        var acceptance = SupervisorDecisionSchema.ResponseSchema
            .GetProperty("properties").GetProperty("stop").GetProperty("properties").GetProperty("acceptance");

        acceptance.GetProperty("type").GetString().ShouldBe("object");
        acceptance.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse("the acceptance object rejects invented fields");
        acceptance.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("command");
        acceptance.GetProperty("properties").GetProperty("command").GetProperty("minItems").GetInt32()
            .ShouldBe(1, "an acceptance command must be a non-empty argv");
    }
}
