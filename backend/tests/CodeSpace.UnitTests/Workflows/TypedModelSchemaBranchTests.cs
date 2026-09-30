using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpace.Core.Services.Tasks.SpecPreview;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

public sealed class TypedModelSchemaBranchTests
{
    [Fact]
    public void The_generator_sees_every_field_an_oracle_branch_declares_or_requires_on_one_flat_acceptance()
    {
        // The per-kind branches no longer reach a structured-output generator: a hosted vLLM backend answered every
        // planner call that carried them with an empty HTTP 500, so the provider is handed PlannerSchema.WireSchema.
        // The branches still VALIDATE each reply — one per oracle kind — and the generator can only emit a field the
        // wire declares, so every field any branch declares OR requires must be declared on the wire's flat acceptance.
        // Declares, not only requires: an optional property that only a branch mentions is just as unreachable, because
        // the flat acceptance is additionalProperties:false and nothing else on the wire would let the generator write it.
        var branches = AcceptanceSchema().GetProperty("oneOf").EnumerateArray().ToArray();
        var kinds = AcceptanceSchema().GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(kind => kind.GetString()).ToArray();

        branches.Select(branch => branch.GetProperty("properties").GetProperty("kind").GetProperty("enum")[0].GetString()).ShouldBe(kinds, ignoreOrder: true, "one validating branch per oracle kind");

        var wire = WireAcceptanceSchema();
        wire.TryGetProperty("oneOf", out _).ShouldBeFalse();
        wire.GetProperty("additionalProperties").ValueKind.ShouldBe(JsonValueKind.False, "premise: a field the wire acceptance does not declare is one the generator cannot write");

        var fields = branches.SelectMany(BranchFields).Distinct().ToArray();
        fields.ShouldContain("kind", "fixture check: the walk reads the branches' own property names");

        foreach (var field in fields)
            wire.GetProperty("properties").TryGetProperty(field, out _).ShouldBeTrue($"a branch declares or requires '{field}', so the wire acceptance must declare it or the generator can never emit it");

        wire.GetProperty("required").EnumerateArray().Select(name => name.GetString()).ShouldBe(new[] { "formatVersion", "kind" }, "the requirement every branch shares stays on the wire");
    }

    [Fact]
    public void The_wire_schema_is_the_validation_schema_minus_only_the_acceptance_branches()
    {
        // Dropping a combinator only removes a constraint, so this equality is what makes the wire a SUPERSET of the
        // contract: it can never forbid a reply the validation schema accepts, and it lost nothing else on the way.
        var expected = JsonNode.Parse(PlannerSchema.ResponseSchema.GetRawText())!;
        expected["properties"]!["subtasks"]!["items"]!["properties"]!["acceptance"]!.AsObject().Remove("oneOf");

        JsonNode.DeepEquals(JsonNode.Parse(PlannerSchema.WireSchema.GetRawText()), expected).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(ValidOracles))]
    public void Every_valid_oracle_reply_is_also_valid_on_the_wire(string acceptance)
    {
        var reply = JsonDocument.Parse($$"""{"goal":"g","subtasks":[{"id":"s1","title":"t","instruction":"i","acceptance":{{acceptance}}}]}""").RootElement;

        JsonSchemaValidator.Validate(reply, PlannerSchema.ResponseSchema).ShouldBeEmpty("precondition: the validation schema accepts this reply");
        JsonSchemaValidator.Validate(reply, PlannerSchema.WireSchema).ShouldBeEmpty("the wire schema may never forbid a reply the validation schema accepts");
    }

    /// <summary>The VALIDATION schema — the one every reply is checked against and the prompt quotes — requires the payload of the oracle kind a reply selects. The decoder is handed the wire form instead, which no longer says so (see <see cref="The_generator_sees_every_field_an_oracle_branch_declares_or_requires_on_one_flat_acceptance"/>).</summary>
    [Theory]
    [InlineData("TestsPass")]
    [InlineData("ArtifactPresent")]
    [InlineData("LlmJudge")]
    [InlineData("CitationsResolve")]
    [InlineData("ArtifactSchema")]
    public void The_validation_schema_itself_requires_the_selected_oracle_payload(string kind)
    {
        var schema = AcceptanceSchema();
        var missing = JsonSerializer.SerializeToElement(new { formatVersion = 2, kind });
        JsonSchemaValidator.Validate(missing, schema).ShouldNotBeEmpty("the validation schema's structural constraints must express the same required payload as the runtime converter");
    }

    [Theory]
    [InlineData("""{"formatVersion":2,"kind":"TestsPass","argv":[]}""")]
    [InlineData("""{"formatVersion":2,"kind":"ArtifactPresent","artifactPaths":[]}""")]
    [InlineData("""{"formatVersion":2,"kind":"TestsPass","argv":["sh"],"artifactPaths":["out.txt"]}""")]
    [InlineData("""{"formatVersion":2,"kind":"LlmJudge","artifactPaths":["out.txt"]}""")]
    [InlineData("""{"formatVersion":2,"kind":"ArtifactSchema","artifactPaths":["out.txt"]}""")]
    public void A_present_but_empty_conflicting_or_incomplete_payload_does_not_satisfy_the_validation_schema(string response) =>
        JsonSchemaValidator.Validate(JsonDocument.Parse(response).RootElement, AcceptanceSchema()).ShouldNotBeEmpty();

    public static TheoryData<string> ValidOracles => new(
        """{"formatVersion":2,"kind":"TestsPass","argv":["sh","","  ","Δ"]}""",
        """{"formatVersion":2,"kind":"ArtifactPresent","artifactPaths":["out.txt"]}""",
        """{"formatVersion":2,"kind":"CitationsResolve","artifactPaths":["out.txt"]}""",
        """{"formatVersion":2,"kind":"LlmJudge","artifactPaths":["out.txt"],"rubric":{"criteria":[{"id":"a","requirement":"explains findings"}]}}""",
        """{"formatVersion":2,"kind":"ArtifactSchema","artifactPaths":["out.txt"],"schema":{"type":"object"}}""");

    [Theory]
    [MemberData(nameof(ValidOracles))]
    public void Every_valid_oracle_remains_expressible_without_altering_its_data(string response) =>
        JsonSchemaValidator.Validate(JsonDocument.Parse(response).RootElement, AcceptanceSchema()).ShouldBeEmpty();

    [Fact]
    public void The_acceptance_type_stays_object_because_a_nullable_one_faults_less_legibly_rather_than_more()
    {
        // Why `acceptance` keeps `"type": "object"` even though the runtime contract now DEGRADES a non-object one.
        // The schema is the model's instruction sheet, so declaring null acceptable would tell it to author a value
        // the contract refuses. And it would not buy a cleaner fault: `required` and `properties` are both skipped
        // for a non-object instance, so each per-kind branch reduces to its `not` — which a null instance trips. The
        // single "expected type 'object' but got null" the advisory explains becomes an unmatched-oneOf spill.
        var schema = AcceptanceSchema();
        schema.GetProperty("type").GetString().ShouldBe("object");
        JsonSchemaValidator.Validate(JsonDocument.Parse("null").RootElement, schema).ShouldHaveSingleItem().ShouldContain("expected type 'object' but got null");

        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema.GetRawText())!;
        fields["type"] = JsonDocument.Parse("""["object","null"]""").RootElement;

        var violations = JsonSchemaValidator.Validate(JsonDocument.Parse("null").RootElement, JsonSerializer.SerializeToElement(fields));

        violations.ShouldNotBeEmpty("a nullable acceptance is still not a valid acceptance — the widening only changes which keyword complains");
        violations.ShouldContain(violation => violation.Contains("matches a forbidden schema"), customMessage: "every branch's `not` trips on a non-object, so the reply is faulted for a payload it never authored");
    }

    [Fact]
    public void A_non_executable_task_can_explicitly_decline_a_literal_source_reference()
    {
        var schema = TaskSpecCompilerSchema.ResponseSchema.GetProperty("properties").GetProperty("acceptanceArgvSource");
        JsonSchemaValidator.Validate(JsonDocument.Parse("null").RootElement, schema).ShouldBeEmpty();
        JsonSchemaValidator.Validate(JsonDocument.Parse("{}").RootElement, schema).ShouldNotBeEmpty();
        JsonSchemaValidator.Validate(JsonDocument.Parse("42").RootElement, schema).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""", "4", false)]
    [InlineData("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""", "4.5", true)]
    [InlineData("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""", "\"4\"", false)]
    [InlineData("""{"anyOf":[{"type":"number"},{"type":"integer"}]}""", "4", true)]
    [InlineData("""{"anyOf":[{"type":"number"},{"type":"integer"}]}""", "null", false)]
    [InlineData("""{"type":["object","null"]}""", "null", true)]
    [InlineData("""{"type":["object","null"]}""", "[]", false)]
    [InlineData("""{"not":{"required":["forbidden"]}}""", "{\"forbidden\":null}", false)]
    [InlineData("""{"not":{"required":["forbidden"]}}""", "{}", true)]
    public void Alternative_schemas_enforce_their_actual_matching_semantics(string schema, string response, bool valid) =>
        (JsonSchemaValidator.Validate(JsonDocument.Parse(response).RootElement, JsonDocument.Parse(schema).RootElement).Count == 0).ShouldBe(valid);

    /// <summary>The acceptance inside the VALIDATION schema, per-kind branches and all: what every reply is checked against and the prompt quotes.</summary>
    private static JsonElement AcceptanceSchema() => PlannerSchema.ResponseSchema.GetProperty("properties").GetProperty("subtasks").GetProperty("items").GetProperty("properties").GetProperty("acceptance");

    /// <summary>The acceptance inside the WIRE schema: the flat, branch-free object the provider's decoder is handed.</summary>
    private static JsonElement WireAcceptanceSchema() => PlannerSchema.WireSchema.GetProperty("properties").GetProperty("subtasks").GetProperty("items").GetProperty("properties").GetProperty("acceptance");

    /// <summary>Every field name one branch mentions: the ones it declares under <c>properties</c> and the ones it requires.</summary>
    private static IEnumerable<string> BranchFields(JsonElement branch) =>
        branch.GetProperty("properties").EnumerateObject().Select(property => property.Name).Concat(branch.GetProperty("required").EnumerateArray().Select(name => name.GetString()!));

    [Fact]
    public void Deeply_branching_schema_validation_is_bounded_and_cannot_turn_exhaustion_into_success()
    {
        var schema = "{\"type\":\"object\"}";
        for (var i = 0; i < 12; i++) schema = "{\"anyOf\":[" + schema + "," + schema + "]}";
        var errors = JsonSchemaValidator.Validate(JsonDocument.Parse("{}").RootElement, JsonDocument.Parse(schema).RootElement);
        errors.Count.ShouldBe(1);
        errors[0].ShouldContain("work budget exceeded");
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("[1]", true)]
    [InlineData("[1,2]", true)]
    [InlineData("[1,2,3]", false)]
    public void Array_cardinality_is_checked_independently_of_item_types(string response, bool valid) =>
        (JsonSchemaValidator.Validate(JsonDocument.Parse(response).RootElement, JsonDocument.Parse("{\"type\":\"array\",\"minItems\":1,\"maxItems\":2}").RootElement).Count == 0).ShouldBe(valid);
}
