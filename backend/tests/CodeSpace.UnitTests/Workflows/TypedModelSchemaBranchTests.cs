using System.Text.Json;
using CodeSpace.Core.Services.Tasks.SpecPreview;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

public sealed class TypedModelSchemaBranchTests
{
    [Fact]
    public void Every_oracle_branch_is_self_describing_for_structured_output_generators()
    {
        var branches = AcceptanceSchema().GetProperty("oneOf").EnumerateArray().ToArray();

        branches.Length.ShouldBe(5);
        foreach (var branch in branches)
        {
            var properties = branch.GetProperty("properties");
            var required = branch.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
            var kind = properties.GetProperty("kind").GetProperty("enum")[0].GetString();
            var payload = kind == "TestsPass" ? "argv" : "artifactPaths";

            properties.TryGetProperty("formatVersion", out _).ShouldBeTrue("a generator may interpret a oneOf branch without merging its parent's properties");
            properties.TryGetProperty(payload, out _).ShouldBeTrue($"the {kind} branch must expose its required payload shape where that requirement is declared");
            required.ShouldContain("formatVersion");
            required.ShouldContain("kind");
            required.ShouldContain(payload);
        }
    }

    [Theory]
    [InlineData("TestsPass")]
    [InlineData("ArtifactPresent")]
    [InlineData("LlmJudge")]
    [InlineData("CitationsResolve")]
    [InlineData("ArtifactSchema")]
    public void The_model_visible_schema_itself_requires_the_selected_oracle_payload(string kind)
    {
        var schema = AcceptanceSchema();
        var missing = JsonSerializer.SerializeToElement(new { formatVersion = 2, kind });
        JsonSchemaValidator.Validate(missing, schema).ShouldNotBeEmpty("model-visible structural constraints must express the same required payload as the runtime converter");
    }

    [Theory]
    [InlineData("""{"formatVersion":2,"kind":"TestsPass","argv":[]}""")]
    [InlineData("""{"formatVersion":2,"kind":"ArtifactPresent","artifactPaths":[]}""")]
    [InlineData("""{"formatVersion":2,"kind":"TestsPass","argv":["sh"],"artifactPaths":["out.txt"]}""")]
    [InlineData("""{"formatVersion":2,"kind":"LlmJudge","artifactPaths":["out.txt"]}""")]
    [InlineData("""{"formatVersion":2,"kind":"ArtifactSchema","artifactPaths":["out.txt"]}""")]
    public void A_present_but_empty_conflicting_or_incomplete_payload_does_not_satisfy_the_model_schema(string response) =>
        JsonSchemaValidator.Validate(JsonDocument.Parse(response).RootElement, AcceptanceSchema()).ShouldNotBeEmpty();

    [Theory]
    [InlineData("""{"formatVersion":2,"kind":"TestsPass","argv":["sh","","  ","Δ"]}""")]
    [InlineData("""{"formatVersion":2,"kind":"ArtifactPresent","artifactPaths":["out.txt"]}""")]
    [InlineData("""{"formatVersion":2,"kind":"CitationsResolve","artifactPaths":["out.txt"]}""")]
    [InlineData("""{"formatVersion":2,"kind":"LlmJudge","artifactPaths":["out.txt"],"rubric":{"criteria":[{"id":"a","requirement":"explains findings"}]}}""")]
    [InlineData("""{"formatVersion":2,"kind":"ArtifactSchema","artifactPaths":["out.txt"],"schema":{"type":"object"}}""")]
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

    private static JsonElement AcceptanceSchema() => PlannerSchema.ResponseSchema.GetProperty("properties").GetProperty("subtasks").GetProperty("items").GetProperty("properties").GetProperty("acceptance");

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
