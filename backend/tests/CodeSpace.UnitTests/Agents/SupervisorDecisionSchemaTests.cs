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
    public void The_kind_enum_is_exactly_the_seven_verbs()
    {
        var kinds = Schema.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        // The seven-verb vocabulary, in order (resolver loop #379 added 'resolve') — a drift here is a contract change a reviewer must see.
        kinds.ShouldBe(new[]
        {
            SupervisorDecisionKinds.Plan,
            SupervisorDecisionKinds.Spawn,
            SupervisorDecisionKinds.Retry,
            SupervisorDecisionKinds.AskHuman,
            SupervisorDecisionKinds.Merge,
            SupervisorDecisionKinds.Resolve,
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
    public void The_plan_delivery_object_is_closed_and_wholly_optional()
    {
        // DC-1: the delivery contract nests INSIDE plan.properties, not at the schema root — it is data the model
        // proposes ALONGSIDE a plan, never its own verb. Closed + optional at every level, matching every sibling
        // plan sub-object (subtasks/phases): a drift here is a contract change a reviewer must see.
        var plan = Schema.GetProperty("properties").GetProperty("plan");
        var delivery = plan.GetProperty("properties").GetProperty("delivery");

        delivery.GetProperty("type").GetString().ShouldBe("object");
        delivery.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse("the delivery object rejects invented fields");
        delivery.GetProperty("properties").TryGetProperty("openPullRequest", out _).ShouldBeTrue();
        delivery.GetProperty("properties").TryGetProperty("targetBranch", out _).ShouldBeTrue();

        plan.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldNotContain("delivery", "a plan that names no delivery preference at all must remain schema-valid");
    }

    [Fact]
    public void A_root_level_rationale_is_declared_for_every_verb()
    {
        // Rationale is a DECISION-level annotation (why + evidence), authored uniformly at the root for every verb —
        // NOT nested in one verb's payload. Pin it at the root, closed, with the two bounded fields, and OPTIONAL
        // (never in `required` — the model may give none). A drift here is a contract change a reviewer must see.
        var rationale = Schema.GetProperty("properties").GetProperty("rationale");

        rationale.GetProperty("type").GetString().ShouldBe("object");
        rationale.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse("the rationale rejects invented fields");
        rationale.GetProperty("properties").TryGetProperty("why", out _).ShouldBeTrue();
        rationale.GetProperty("properties").TryGetProperty("evidence", out _).ShouldBeTrue();

        Schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldNotContain("rationale", "rationale is strongly recommended but optional — a decision may author none");
    }

    [Fact]
    public void The_retry_payload_no_longer_nests_a_rationale()
    {
        // The rationale hoisted from retry's sub-object to the decision root — retry must no longer advertise it, or the
        // model could author it in two places (ambiguous) and the projector would double-count.
        var retry = Schema.GetProperty("properties").GetProperty("retry");

        retry.GetProperty("properties").TryGetProperty("rationale", out _).ShouldBeFalse("retry's rationale moved to the decision root — it must not remain nested");
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

    [Fact]
    public void Merge_advertises_no_subtask_subset_it_cannot_honor()
    {
        // P2-4 honesty: the merge executor folds ALL prior agent results, so the schema must NOT advertise a
        // subtaskIds subset the executor silently ignores. Selective merge returns with the richer-synthesis slice.
        var merge = Schema.GetProperty("properties").GetProperty("merge");

        merge.GetProperty("properties").TryGetProperty("subtaskIds", out _).ShouldBeFalse("merge must not expose a subtaskIds subset it cannot honor");
        merge.GetProperty("required").EnumerateArray().Any().ShouldBeFalse("merge requires no fields — it folds all prior results");
    }
}
