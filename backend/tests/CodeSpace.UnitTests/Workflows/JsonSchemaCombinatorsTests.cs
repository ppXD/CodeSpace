using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Llm;
using CodeSpace.Core.Services.Tasks.SpecPreview;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the combinator-free WIRE form of a structured-output schema: what <see cref="JsonSchemaCombinators.Strip"/>
/// removes and what it must leave alone, and the portability rule that every schema the code hands a model reaches the
/// provider with no combinator at any depth. A hosted vLLM backend compiles that schema into a decoding grammar, and a
/// combinator shape it cannot compile costs the whole call an empty HTTP 500 — so the rule is pinned per schema rather
/// than rediscovered per outage.
/// </summary>
[Trait("Category", "Unit")]
public sealed class JsonSchemaCombinatorsTests
{
    private static readonly string[] Combinators = ["oneOf", "anyOf", "allOf", "not", "if", "then", "else"];

    /// <summary>
    /// Every schema backend/src hands a model — the value of each <c>JsonSchema =</c> assignment, plus the coordinator's
    /// schema, which reaches one through <c>llm.complete</c>'s <c>responseSchema</c> — in the form the provider receives
    /// (<c>WireJsonSchema ?? JsonSchema</c>). The planner is the one caller whose wire form differs from its contract.
    /// An operator-authored <c>llm.complete</c> schema is the operator's own and is not listed.
    /// </summary>
    private static readonly Dictionary<string, JsonElement> WireForms = new()
    {
        ["ArbiterDecisionSchema.ResponseSchema"] = ArbiterDecisionSchema.ResponseSchema,
        ["CoordinatorSchema.ResponseSchema"] = CoordinatorSchema.ResponseSchema,
        ["CriticSchema.GateSchema"] = CriticSchema.GateSchema,
        ["CriticSchema.ImproveSchema"] = CriticSchema.ImproveSchema,
        ["LessonDistillationSchema.ResponseSchema"] = LessonDistillationSchema.ResponseSchema,
        ["LlmEffortClassifierSchema.ResponseSchema"] = LlmEffortClassifierSchema.ResponseSchema,
        ["LlmLessonRelevanceEvaluator.ResponseSchema"] = LlmLessonRelevanceEvaluator.ResponseSchema,
        ["LlmRubricJudge.RubricVerdictSchema"] = LlmRubricJudge.RubricVerdictSchema,
        ["LlmSupervisorDecider.TapeSummarySchema"] = LlmSupervisorDecider.TapeSummarySchema,
        ["ModelTieringSchema.ResponseSchema"] = ModelTieringSchema.ResponseSchema,
        ["PlannerSchema.WireSchema"] = PlannerSchema.WireSchema,
        ["SupervisorDecisionSchema.ResponseSchema"] = SupervisorDecisionSchema.ResponseSchema,
        ["TaskSpecCompilerSchema.ResponseSchema"] = TaskSpecCompilerSchema.ResponseSchema,
        ["TaskSpecCompilerSchema.ReviewSchema"] = TaskSpecCompilerSchema.ReviewSchema,
    };

    /// <summary>Schemas that only ever VALIDATE: their call site hands the provider a wire form instead (<see cref="PlannerSchema.WireSchema"/>), so they may carry the combinators the wire cannot.</summary>
    private static readonly string[] ValidationOnly = ["PlannerSchema.ResponseSchema"];

    public static TheoryData<string> WireFormNames => new(WireForms.Keys);

    [Theory]
    [MemberData(nameof(WireFormNames))]
    public void Every_schema_the_code_sends_to_a_model_is_combinator_free_on_the_wire(string schema) =>
        CombinatorPaths(WireForms[schema]).ShouldBeEmpty($"{schema} reaches the provider's constrained decoder; carry the combinator in a validation-only JsonSchema and send JsonSchemaCombinators.Strip of it as the WireJsonSchema");

    [Fact]
    public void Every_schema_constant_in_core_is_either_sent_on_the_wire_or_only_validates()
    {
        // The guard above is only as good as its list. Every schema a model is handed today is a static *Schema field,
        // so a new one lands here and must be sorted: sent to a provider (combinator-free) or validation-only.
        var declared = typeof(PlannerSchema).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(field => field.FieldType == typeof(JsonElement) && field.Name.EndsWith("Schema", StringComparison.Ordinal)).Select(field => $"{type.Name}.{field.Name}"));

        declared.ShouldBe(WireForms.Keys.Concat(ValidationOnly), ignoreOrder: true);
    }

    [Theory]
    [InlineData("oneOf")]
    [InlineData("anyOf")]
    [InlineData("allOf")]
    [InlineData("not")]
    [InlineData("if")]
    [InlineData("then")]
    [InlineData("else")]
    public void Strip_removes_a_combinator_at_every_schema_position_and_nothing_beside_it(string keyword)
    {
        var stripped = JsonSchemaCombinators.Strip(JsonDocument.Parse(SchemaAtEveryPosition(keyword)).RootElement);

        CombinatorPaths(stripped).ShouldBeEmpty();
        JsonNode.DeepEquals(JsonNode.Parse(stripped.GetRawText()), JsonNode.Parse(SchemaAtEveryPosition(keyword: null))).ShouldBeTrue("only the combinator goes; every sibling keyword stays exactly as written");
    }

    [Fact]
    public void Strip_keeps_property_names_and_literal_values_that_happen_to_spell_a_combinator()
    {
        // A property called "not" is still a property: dropping its declaration under additionalProperties:false would
        // forbid a field the contract allows, which is the one thing a wire schema may never do. Literals are data.
        const string schema = """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": { "not": { "type": "string" }, "if": { "type": "boolean" }, "then": { "type": "object", "default": { "oneOf": 1 } } },
              "required": ["not", "if"],
              "examples": [{ "not": "x", "if": true, "then": { "anyOf": [] } }]
            }
            """;

        JsonNode.DeepEquals(JsonNode.Parse(JsonSchemaCombinators.Strip(JsonDocument.Parse(schema).RootElement).GetRawText()), JsonNode.Parse(schema)).ShouldBeTrue();
    }

    /// <summary>Every path at which a combinator keyword appears as an object key, at ANY depth. Deliberately blind to what a key means, so it is stricter than the stripper it checks.</summary>
    internal static IReadOnlyList<string> CombinatorPaths(JsonElement node, string path = "$") => node.ValueKind switch
    {
        JsonValueKind.Object => node.EnumerateObject().SelectMany(property => (Combinators.Contains(property.Name) ? [$"{path}.{property.Name}"] : Array.Empty<string>()).Concat(CombinatorPaths(property.Value, $"{path}.{property.Name}"))).ToArray(),
        JsonValueKind.Array => node.EnumerateArray().SelectMany((item, index) => CombinatorPaths(item, $"{path}[{index}]")).ToArray(),
        _ => [],
    };

    /// <summary>One schema with <paramref name="keyword"/> beside a sibling at every position a subschema can sit — or, for a null keyword, the same schema written without it.</summary>
    private static string SchemaAtEveryPosition(string? keyword)
    {
        var value = keyword?.EndsWith("Of", StringComparison.Ordinal) == true ? """[{ "required": ["a"] }]""" : """{ "required": ["a"] }""";
        var at = keyword is null ? "" : $"\"{keyword}\": {value}, ";

        return $$"""
            {
              {{at}}"type": "object",
              "properties": { "a": { {{at}}"type": "string" }, "list": { "type": "array", "items": { {{at}}"minLength": 1 }, "prefixItems": [{ {{at}}"maxLength": 9 }], "contains": { {{at}}"const": "x" } } },
              "patternProperties": { "^x-": { {{at}}"type": "number" } },
              "additionalProperties": { {{at}}"type": "boolean" },
              "propertyNames": { {{at}}"pattern": "^[a-z-]+$" },
              "dependentSchemas": { "a": { {{at}}"required": ["list"] } },
              "unevaluatedProperties": { {{at}}"type": "null" },
              "$defs": { "d": { {{at}}"type": "integer" } },
              "definitions": { "d": { {{at}}"type": "integer" } }
            }
            """;
    }
}
