using System.Text.Json;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins the PR-E E3 supervisor decision COMMIT-CONTRACT (<see cref="SupervisorDecisionSchema"/>). The
/// schema is what the structured-LLM call is constrained to + what a reviewer must see change. Pins: the full
/// six-verb vocabulary is present; every payload object is <c>additionalProperties:false</c>; and the
/// NO-GRAPH-REF guard — the schema carries NO node-id / type-key / workflow-id / run-id field anywhere (the
/// model emits a DECISION the server turns into a side effect; it never addresses graph topology).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDecisionSchemaTests
{
    private static readonly JsonElement Schema = SupervisorDecisionSchema.ResponseSchema;

    [Fact]
    public void The_schema_root_is_a_closed_object_keyed_on_kind()
    {
        Schema.GetProperty("type").GetString().ShouldBe("object");
        Schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse("the root rejects unknown fields");
        Schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("kind");
    }

    [Fact]
    public void The_kind_enum_is_exactly_the_six_verbs()
    {
        var kinds = Schema.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        // The six-verb vocabulary, in order — a drift here is a contract change a reviewer must see.
        kinds.ShouldBe(new[]
        {
            SupervisorDecisionKinds.Plan,
            SupervisorDecisionKinds.Spawn,
            SupervisorDecisionKinds.Retry,
            SupervisorDecisionKinds.AskHuman,
            SupervisorDecisionKinds.Merge,
            SupervisorDecisionKinds.Stop,
        });
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("spawn")]
    [InlineData("retry")]
    [InlineData("askHuman")]
    [InlineData("merge")]
    [InlineData("stop")]
    public void Every_verb_payload_object_is_closed(string verbProperty)
    {
        var payload = Schema.GetProperty("properties").GetProperty(verbProperty);

        payload.GetProperty("type").GetString().ShouldBe("object");
        payload.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse($"the {verbProperty} payload rejects invented fields");
    }

    [Fact]
    public void The_schema_carries_no_graph_topology_reference_anywhere()
    {
        // THE no-graph-ref guard (must-fix): the model NEVER names a node id, a type key, a workflow/run id, or
        // an iteration key. Those are SERVER-derived (the ledger key, the agent-run waits, the node id). Scan the
        // ENTIRE schema's raw text for any such field name — a future widening that leaks topology fails here.
        var raw = Schema.GetRawText();

        foreach (var banned in new[] { "nodeId", "node_id", "typeKey", "type_key", "workflowId", "workflow_id", "runId", "run_id", "iterationKey", "iteration_key", "waitId", "wait_id" })
            raw.ShouldNotContain(banned, Case.Insensitive, $"the decision schema must not expose graph topology ('{banned}') — the model emits a verb, the server derives the wiring");
    }

    [Fact]
    public void Bounded_fan_out_subtasks_and_spawn_ids_are_capped()
    {
        Schema.GetProperty("properties").GetProperty("plan").GetProperty("properties").GetProperty("subtasks").GetProperty("maxItems").GetInt32().ShouldBe(20);
        Schema.GetProperty("properties").GetProperty("spawn").GetProperty("properties").GetProperty("subtaskIds").GetProperty("maxItems").GetInt32().ShouldBe(20);
    }
}
