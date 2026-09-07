using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Plans;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class PlannerAcceptanceMappingTests
{
    [Fact]
    public void Model_schema_separates_argv_from_paths_and_requires_a_known_format_and_oracle()
    {
        var acceptance = PlannerSchema.ResponseSchema.GetProperty("properties").GetProperty("subtasks").GetProperty("items").GetProperty("properties").GetProperty("acceptance");
        acceptance.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ShouldBe(new[] { "formatVersion", "kind" });
        var properties = acceptance.GetProperty("properties");
        properties.TryGetProperty("command", out _).ShouldBeFalse("a fresh model response must not overload command as either argv or file paths");
        properties.GetProperty("argv").GetProperty("type").GetString().ShouldBe("array");
        properties.GetProperty("artifactPaths").GetProperty("type").GetString().ShouldBe("array");
        properties.GetProperty("formatVersion").GetProperty("enum")[0].GetInt32().ShouldBe(2);
    }

    [Fact]
    public void Exact_argv_survives_mapping_and_the_persisted_plan_without_promoting_or_trimming_arguments()
    {
        var argv = new[] { "sh", "-c", "printf '<%s>' \"$1\"", "--", "", "  ", "a b", "$(not-executed-by-mapping)" };
        var item = WorkPlanItem.From(Parse(JsonSerializer.SerializeToElement(new { formatVersion = 2, kind = "TestsPass", argv })));
        item.Acceptance!.Kind.ShouldBe(BenchmarkGradingKind.TestsPass);
        item.Acceptance.Command.ShouldBe(argv);
        var persisted = JsonSerializer.Serialize(item, AgentJson.Options);
        var restored = JsonSerializer.Deserialize<WorkPlanItem>(persisted, AgentJson.Options)!;
        restored.Acceptance!.Command.ShouldBe(argv);
        ContractHashing.Hash(restored.Acceptance, AgentJson.Options).ShouldBe(ContractHashing.Hash(new SupervisorAcceptanceSpec { Kind = BenchmarkGradingKind.TestsPass, Command = argv }, AgentJson.Options));
        persisted.ShouldNotContain("formatVersion", customMessage: "the model wire version must not silently change the existing runtime contract hash vocabulary");
    }

    [Theory]
    [InlineData("ArtifactPresent", "{}")]
    [InlineData("LlmJudge", "{\"rubric\":{\"criteria\":[{\"id\":\"coverage\",\"requirement\":\"explains the evidence\"}],\"threshold\":1}}")]
    [InlineData("CitationsResolve", "{}")]
    [InlineData("ArtifactSchema", "{\"schema\":{\"type\":\"object\",\"required\":[\"answer\"]}}")]
    public void Every_file_oracle_remains_expressible_and_valid_literal_filenames_are_not_guessed_to_be_commands(string kind, string extras)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extras)!;
        payload["formatVersion"] = JsonSerializer.SerializeToElement(2);
        payload["kind"] = JsonSerializer.SerializeToElement(kind);
        payload["artifactPaths"] = JsonSerializer.SerializeToElement(new[] { "test", "-f", "notes with spaces.md" });
        var acceptance = Parse(JsonSerializer.SerializeToElement(payload)).Acceptance!;
        acceptance.Kind.ShouldBe(Enum.Parse<BenchmarkGradingKind>(kind));
        acceptance.Command.ShouldBe(new[] { "test", "-f", "notes with spaces.md" }, "file intent is explicitly typed, never inferred from names that resemble executables or flags");
        AgentAcceptanceContract.ValidateAuthored(acceptance).ShouldBeNull();
    }

    [Theory]
    [InlineData("{\"kind\":\"ArtifactPresent\",\"command\":[\"test\",\"-f\",\"report.md\"]}")]
    [InlineData("{\"formatVersion\":1,\"kind\":\"TestsPass\",\"argv\":[\"true\"]}")]
    [InlineData("{\"formatVersion\":99,\"kind\":\"TestsPass\",\"argv\":[\"true\"]}")]
    [InlineData("{\"formatVersion\":2,\"argv\":[\"true\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"true\"],\"artifactPaths\":[\"report.md\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"ArtifactPresent\",\"argv\":[\"test\",\"-f\",\"report.md\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"artifactPaths\":[]}")]   // an EMPTY other payload is still the reinterpretable shape — rejected before "the right payload is absent" is ever considered
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"\",\"true\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"true\",null]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"true\",\"x\\u0000y\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"ArtifactPresent\",\"artifactPaths\":[\"\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"LlmJudge\",\"artifactPaths\":[\"report.md\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"LlmJudge\",\"artifactPaths\":[\"report.md\"],\"rubric\":{\"criteria\":[null]}}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"ArtifactSchema\",\"artifactPaths\":[\"report.json\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"UnknownOracle\",\"argv\":[\"true\"]}")]
    public void An_unknown_legacy_or_incomplete_fresh_response_is_rejected_instead_of_dropping_or_reinterpreting_the_contract(string payload)
    {
        Should.Throw<InvalidOperationException>(() => Parse(JsonDocument.Parse(payload).RootElement));
    }

    [Fact]
    public void Existing_persisted_v1_plan_remains_readable_with_the_same_bytes_and_hash()
    {
        const string stored = "{\"id\":\"item\",\"title\":\"Keep\",\"instruction\":\"Keep the report\",\"acceptance\":{\"command\":[\"report.md\"],\"kind\":\"ArtifactPresent\"}}";
        var item = JsonSerializer.Deserialize<WorkPlanItem>(stored, AgentJson.Options)!;
        item.Acceptance!.Command.ShouldBe(new[] { "report.md" });
        JsonSerializer.Serialize(item, AgentJson.Options).ShouldBe(stored);
        ContractHashing.Hash(item.Acceptance, AgentJson.Options).ShouldBe(ContractHashing.Hash(JsonDocument.Parse(stored).RootElement.GetProperty("acceptance"), AgentJson.Options));
    }

    [Fact]
    public void Case_insensitive_subtask_binding_cannot_bypass_the_fresh_acceptance_version_boundary()
    {
        var legacy = JsonDocument.Parse("{\"goal\":\"g\",\"subtasks\":[{\"id\":\"s\",\"title\":\"T\",\"instruction\":\"I\",\"Acceptance\":{\"kind\":\"ArtifactPresent\",\"command\":[\"test\",\"-f\",\"report.md\"]}}]}").RootElement;
        Should.Throw<InvalidOperationException>(() => LlmWorkflowPlanner.Deserialize(legacy));
    }

    [Theory]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\"}", "TestsPass", "argv")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[]}", "TestsPass", "argv")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"ArtifactPresent\"}", "ArtifactPresent", "artifactPaths")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"CitationsResolve\",\"artifactPaths\":[]}", "CitationsResolve", "artifactPaths")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"LlmJudge\",\"rubric\":{\"criteria\":[{\"id\":\"c\",\"requirement\":\"cites sources\"}]}}", "LlmJudge", "artifactPaths")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"ArtifactSchema\",\"schema\":{\"type\":\"object\"}}", "ArtifactSchema", "artifactPaths")]
    public void An_oracle_chosen_with_no_payload_costs_that_subtask_its_oracle_and_never_the_whole_plan(string acceptance, string kind, string payloadName)
    {
        // The regression this pins: one subtask skipping one payload FAILED the planner node, so a plan-map launch
        // died at planning over a model-quality miss. The plan is kept; the item is graded by nothing, and says so.
        var plan = LlmWorkflowPlanner.Deserialize(Reply(acceptance));

        plan.Subtasks.Select(subtask => subtask.Id).ShouldBe(new[] { "item", "sibling" }, "the sibling subtasks are not evidence about the one bad payload");
        plan.Subtasks[0].Acceptance.ShouldBeNull("an oracle with no payload is no oracle — never a guessed or repaired one");
        plan.Subtasks[1].Acceptance!.Command.ShouldBe(new[] { "true" }, "a well-formed sibling contract survives untouched");

        var drop = plan.DroppedAcceptances.ShouldHaveSingleItem();
        drop.SubtaskId.ShouldBe("item", "an unnamed drop is indistinguishable from an acceptance the planner never wrote");
        drop.Kind.ShouldBe(Enum.Parse<BenchmarkGradingKind>(kind));
        drop.Reason.ShouldContain(payloadName);
    }

    [Fact]
    public void A_clean_plan_carries_no_drop_record_at_all()
    {
        var plan = LlmWorkflowPlanner.Deserialize(Reply("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"dotnet\",\"test\"]}"));

        plan.DroppedAcceptances.ShouldBeNull("null-omitted keeps a clean plan's bytes identical to before");
        JsonSerializer.SerializeToElement(plan, AgentJson.Options).TryGetProperty("droppedAcceptances", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_dropped_acceptance_serializes_as_a_named_wire_fact()
    {
        // The record's own wire shape (the node's TOP-LEVEL output key is pinned end-to-end in
        // PlanAuthorNodeFlowTests, against the real node's OutputsJson — this arm is the DTO half only).
        var plan = LlmWorkflowPlanner.Deserialize(Reply("{\"formatVersion\":2,\"kind\":\"TestsPass\"}"));

        var drop = JsonSerializer.SerializeToElement(plan, AgentJson.Options).GetProperty("droppedAcceptances")[0];

        drop.GetProperty("subtaskId").GetString().ShouldBe("item");
        drop.GetProperty("kind").GetString().ShouldBe("TestsPass", "the wire kind is the same vocabulary the acceptance contract uses");
        drop.GetProperty("reason").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("[{\"subtaskId\":\"ghost\",\"kind\":\"TestsPass\",\"reason\":\"invented\"}]")]
    [InlineData("[{}]")]
    [InlineData("[]")]
    [InlineData("\"not even an array\"")]
    public void A_model_authored_drop_record_is_discarded_rather_than_believed_or_fatal(string authored)
    {
        // droppedAcceptances is SERVER-stamped, like authoredByModel beside it — the model schema does not declare it.
        // But the schema check is deliberately lenient on additionalProperties and these read options disallow no
        // unmapped member, so a reply that invents the field would BIND: a fabricated defect report about a plan that
        // has none. `[{}]` was worse still — a required-member bind failure, i.e. a NEW plan-killer inside the very
        // change that exists to stop plans dying over model-quality misses.
        var plan = LlmWorkflowPlanner.Deserialize(ReplyWith("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"true\"]}", authored));

        plan.Subtasks[0].Acceptance.ShouldNotBeNull("the plan itself is untouched — only the model's claim about defects is refused");
        plan.DroppedAcceptances.ShouldBeNull("a clean bind stamps null unconditionally; the model's authored value is never carried");
    }

    [Fact]
    public void The_plain_read_options_still_bind_a_model_authored_drop_record_which_is_why_the_stamp_is_unconditional()
    {
        // Two mechanisms guard one server-stamped field, and this pins WHY both stay. The response boundary's own
        // options refuse to read the field at all (so `[{}]` cannot fail a required-member bind and kill the plan);
        // the options they are LAYERED ON bind it happily, as this proves — which is why Deserialize stamps the field
        // unconditionally, like its AuthoredByModel / LessonArm siblings, rather than trusting one read path. If this
        // test ever fails, the hazard is gone and the redundancy can be reconsidered on purpose.
        const string authored = "{\"goal\":\"verify\",\"subtasks\":[{\"id\":\"item\",\"title\":\"Item\",\"instruction\":\"Do the work\"}],"
                              + "\"droppedAcceptances\":[{\"subtaskId\":\"ghost\",\"kind\":\"TestsPass\",\"reason\":\"invented\"}]}";

        var bound = JsonSerializer.Deserialize<PlannedWorkflow>(authored, PlannerSchema.Options);

        bound!.DroppedAcceptances.ShouldHaveSingleItem().SubtaskId.ShouldBe("ghost",
            "the schema check is lenient on additionalProperties and these options disallow no unmapped member — a model CAN author this field on the wire");
    }

    [Fact]
    public void The_server_stamp_overwrites_a_model_authored_drop_record_rather_than_merging_it()
    {
        var plan = LlmWorkflowPlanner.Deserialize(ReplyWith("{\"formatVersion\":2,\"kind\":\"TestsPass\"}", "[{\"subtaskId\":\"ghost\",\"kind\":\"LlmJudge\",\"reason\":\"invented\"}]"));

        plan.DroppedAcceptances.ShouldHaveSingleItem().SubtaskId.ShouldBe("item",
            "the stamp is the server's OWN bind — a merge would let a model add defect reports for subtasks that graded fine");
    }

    [Theory]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\"}", true)]
    [InlineData("{\"formatVersion\":2,\"kind\":\"ArtifactPresent\",\"artifactPaths\":[]}", true)]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"dotnet\",\"test\"]}", false)]
    [InlineData("{\"formatVersion\":2,\"kind\":\"ArtifactPresent\",\"argv\":[\"report.md\"]}", false)]
    [InlineData("{\"formatVersion\":2,\"kind\":\"UnknownOracle\"}", false)]
    public void The_unbound_payload_earns_a_reask_while_a_fatal_violation_stays_the_validators_business(string acceptance, bool advised)
    {
        // The two severities are asked separately: the ADVISOR is what makes the provider re-ask (#1837 behaviour is
        // kept for exactly this defect), and only the VALIDATOR can turn a reply into a Malformed fault.
        var reply = Reply(acceptance);

        LlmWorkflowPlanner.AdviseModelResponse(reply).Count.ShouldBe(advised ? 1 : 0);

        if (advised) LlmWorkflowPlanner.ValidateModelResponse(reply).ShouldBeEmpty("a droppable defect must never be the reason a plan dies");
    }

    [Fact]
    public void The_reask_advice_names_the_subtask_and_both_ways_out()
    {
        var advice = LlmWorkflowPlanner.AdviseModelResponse(Reply("{\"formatVersion\":2,\"kind\":\"LlmJudge\",\"rubric\":{\"criteria\":[{\"id\":\"c\",\"requirement\":\"cites sources\"}]}}")).ShouldHaveSingleItem();

        advice.Message.ShouldContain("'item'", customMessage: "\"somewhere in your plan\" is not a correction a model can act on");
        advice.Message.ShouldContain("LlmJudge");
        advice.Message.ShouldContain("artifactPaths");
        advice.Message.ShouldContain("omit", customMessage: "omitting acceptance is the honest alternative to inventing a payload");
    }

    [Theory]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\"}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"ArtifactPresent\",\"artifactPaths\":[]}")]
    // The schema reports an unmatched `oneOf` by spilling its CLOSEST branch's violations, and for a payload-less
    // LlmJudge the closest branch is a different kind's — so the reply is faulted for a `kind` the model never got
    // wrong. That spill is why attribution is scoped to the acceptance the contract itself calls droppable rather
    // than matched on keywords: "missing payload" and "bad kind" are indistinguishable in the violation text here.
    [InlineData("{\"formatVersion\":2,\"kind\":\"LlmJudge\"}")]
    public void The_advice_claims_the_position_the_schema_reports_the_same_absence_at(string acceptance)
    {
        // The two checkers name ONE defect: this contract calls it droppable, the model-visible schema faults the very
        // same acceptance (no per-kind oneOf branch matches a payload-less one; an empty payload misses its minItems).
        // The claimed path is how the transport knows they are the same, so it must be the POSITIONAL path the schema
        // walks — the model's own id appears nowhere in it — and it must be the DEGRADED subtask's, not the plan's.
        var advice = LlmWorkflowPlanner.AdviseModelResponse(ReplySecondUnbound(acceptance)).ShouldHaveSingleItem();

        advice.Path.ShouldBe("$.subtasks[1].acceptance", "a claim on the wrong index would silence a sibling's fatal defect and leave this one fatal");

        var atClaim = JsonSchemaValidator.Validate(ReplySecondUnbound(acceptance), PlannerSchema.ResponseSchema);
        atClaim.ShouldNotBeEmpty("the schema must still fault the absent payload — the degrade is an interpretation of that fault, not a hole in it");
        atClaim.ShouldAllBe(violation => JsonSchemaValidator.PathOf(violation).StartsWith(advice.Path), "every violation this reply raises is the claimed defect; anything else would be a fatal the test is not about");
    }

    private static PlannedSubtask Parse(JsonElement acceptance) => LlmWorkflowPlanner.Deserialize(JsonSerializer.SerializeToElement(new { goal = "verify", subtasks = new[] { new { id = "item", title = "Item", instruction = "Do the work", acceptance } } })).Subtasks.Single();

    /// <summary>A two-subtask reply: the first carries <paramref name="acceptance"/> verbatim, the second a well-formed contract — so a test can tell "this item degraded" apart from "the plan collapsed".</summary>
    private static JsonElement Reply(string acceptance) => ReplyWith(acceptance, null);

    /// <summary>The mirror of <see cref="Reply"/>: the WELL-FORMED subtask comes first and <paramref name="acceptance"/> second, so a claim pinned to an index cannot pass by accident on a plan whose only subtask is at 0.</summary>
    private static JsonElement ReplySecondUnbound(string acceptance) => JsonDocument.Parse(
        "{\"goal\":\"verify\",\"subtasks\":["
      + "{\"id\":\"sibling\",\"title\":\"Sibling\",\"instruction\":\"Do the other work\",\"acceptance\":{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"true\"]}},"
      + "{\"id\":\"item\",\"title\":\"Item\",\"instruction\":\"Do the work\",\"acceptance\":" + acceptance + "}"
      + "]}").RootElement;

    /// <summary>The same reply with a model-authored <c>droppedAcceptances</c> appended verbatim — the server-stamped key a reply must never be able to speak for.</summary>
    private static JsonElement ReplyWith(string acceptance, string? droppedAcceptances) => JsonDocument.Parse(
        "{\"goal\":\"verify\",\"subtasks\":["
      + "{\"id\":\"item\",\"title\":\"Item\",\"instruction\":\"Do the work\",\"acceptance\":" + acceptance + "},"
      + "{\"id\":\"sibling\",\"title\":\"Sibling\",\"instruction\":\"Do the other work\",\"acceptance\":{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"true\"]}}"
      + "]" + (droppedAcceptances is null ? "" : ",\"droppedAcceptances\":" + droppedAcceptances) + "}").RootElement;
}
