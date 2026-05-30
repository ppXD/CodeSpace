using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pin every supported operator + truthy fallback. The grammar is narrow on purpose
/// (no boolean composition, no parens) — any future expansion happens here first so
/// the tests document the new semantics before they exist.
/// </summary>
[Trait("Category", "Unit")]
public class ConditionEvaluatorTests
{
    private static NodeRunScope MakeScope() => new()
    {
        Trigger = ParseDict("""
            {
              "number": 42,
              "title": "Fix bug",
              "open": true,
              "tags": ["feature", "bug"],
              "empty_str": "",
              "empty_arr": []
            }
            """)
    };

    [Theory]
    // Numeric comparisons
    [InlineData("{{trigger.number}} > 10", true)]
    [InlineData("{{trigger.number}} > 100", false)]
    [InlineData("{{trigger.number}} >= 42", true)]
    [InlineData("{{trigger.number}} < 50", true)]
    [InlineData("{{trigger.number}} <= 41", false)]
    [InlineData("{{trigger.number}} == 42", true)]
    [InlineData("{{trigger.number}} != 42", false)]
    // String equality (quoted + bare)
    [InlineData("{{trigger.title}} == \"Fix bug\"", true)]
    [InlineData("{{trigger.title}} == 'Fix bug'", true)]
    [InlineData("{{trigger.title}} != \"Other\"", true)]
    // String contains / startsWith / endsWith (case-insensitive)
    [InlineData("{{trigger.title}} contains \"BUG\"", true)]
    [InlineData("{{trigger.title}} startsWith \"fix\"", true)]
    [InlineData("{{trigger.title}} endsWith \"bug\"", true)]
    [InlineData("{{trigger.title}} contains \"missing\"", false)]
    // Booleans
    [InlineData("{{trigger.open}} == true", true)]
    [InlineData("{{trigger.open}} == false", false)]
    // is_empty / is_not_empty
    [InlineData("{{trigger.empty_str}} is_empty", true)]
    [InlineData("{{trigger.title}} is_empty", false)]
    [InlineData("{{trigger.empty_arr}} is_empty", true)]
    [InlineData("{{trigger.tags}} is_empty", false)]
    [InlineData("{{trigger.tags}} is_not_empty", true)]
    [InlineData("{{trigger.empty_arr}} is_not_empty", false)]
    // Bare truthiness
    [InlineData("{{trigger.open}}", true)]
    [InlineData("{{trigger.empty_str}}", false)]
    [InlineData("{{trigger.empty_arr}}", false)]
    [InlineData("{{trigger.tags}}", true)]
    [InlineData("{{trigger.missing}}", false)]
    // Literals
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"some text\"", true)]
    [InlineData("\"\"", false)]
    public void Evaluate(string expression, bool expected)
    {
        ConditionEvaluator.Evaluate(expression, MakeScope()).ShouldBe(expected);
    }

    [Fact]
    public void Blank_expression_is_false()
    {
        ConditionEvaluator.Evaluate("", MakeScope()).ShouldBeFalse();
        ConditionEvaluator.Evaluate("   ", MakeScope()).ShouldBeFalse();
    }

    [Fact]
    public void Number_long_vs_double_equality_works_across_types()
    {
        var scope = new NodeRunScope { Trigger = ParseDict("""{"count":5}""") };
        ConditionEvaluator.Evaluate("{{trigger.count}} == 5.0", scope).ShouldBeTrue();
    }

    // ── CompareValues — the structured operator vocabulary (flow.loop termination) ──

    [Theory]
    [InlineData("\"reasoning DONE here\"", "contains", "DONE", true)]
    [InlineData("\"still thinking\"", "contains", "DONE", false)]
    [InlineData("\"still thinking\"", "not_contains", "DONE", true)]
    [InlineData("\"reasoning DONE\"", "not_contains", "DONE", false)]
    [InlineData("\"ship it\"", "startsWith", "ship", true)]
    [InlineData("\"ship it\"", "endsWith", "it", true)]
    [InlineData("\"ship it\"", "endsWith", "ship", false)]
    [InlineData("\"ready\"", "eq", "ready", true)]
    [InlineData("\"ready\"", "neq", "ready", false)]
    [InlineData("42", "eq", "42", true)]                 // number left vs string literal → stringwise equal
    [InlineData("\"\"", "is_empty", null, true)]
    [InlineData("\"x\"", "is_empty", null, false)]
    [InlineData("\"x\"", "is_not_empty", null, true)]
    [InlineData("\"anything\"", "no_such_op", "x", false)] // unknown operator fails closed
    // Case-insensitive string ops (matters for LLM output matching).
    [InlineData("\"reasoning DONE here\"", "contains", "done", true)]
    [InlineData("\"SHIP IT\"", "startsWith", "ship", true)]
    [InlineData("\"SHIP IT\"", "endsWith", "It", true)]
    [InlineData("\"Ready\"", "eq", "ready", false)] // eq stays case-SENSITIVE (ordinal)
    // Non-string lefts get stringified before string ops.
    [InlineData("42", "contains", "4", true)]
    [InlineData("42", "startsWith", "4", true)]
    [InlineData("true", "eq", "true", true)]
    [InlineData("3.5", "eq", "3.5", true)]
    // contains/startsWith with an empty needle is vacuously true (String.Contains("") == true).
    [InlineData("\"x\"", "contains", "", true)]
    [InlineData("\"x\"", "contains", null, true)]
    public void CompareValues_maps_each_operator(string leftJson, string op, string? right, bool expected)
    {
        var left = JsonDocument.Parse(leftJson).RootElement;
        ConditionEvaluator.CompareValues(op, left, right).ShouldBe(expected);
    }

    [Fact]
    public void CompareValues_emptiness_works_for_strings_arrays_and_objects()
    {
        // is_empty / is_not_empty must reflect structural emptiness for every JSON kind a loop
        // variable or body output can take, not just strings.
        ConditionEvaluator.CompareValues("is_empty", JsonDocument.Parse("\"\"").RootElement, null).ShouldBeTrue();
        ConditionEvaluator.CompareValues("is_empty", JsonDocument.Parse("[]").RootElement, null).ShouldBeTrue();
        ConditionEvaluator.CompareValues("is_empty", JsonDocument.Parse("{}").RootElement, null).ShouldBeTrue();

        ConditionEvaluator.CompareValues("is_not_empty", JsonDocument.Parse("[1,2]").RootElement, null).ShouldBeTrue();
        ConditionEvaluator.CompareValues("is_not_empty", JsonDocument.Parse("\"x\"").RootElement, null).ShouldBeTrue();
        // A number/bool isn't a "container" — neither empty nor meaningfully not-empty by length;
        // pin the actual behaviour so a future refactor can't silently flip it.
        ConditionEvaluator.CompareValues("is_empty", JsonDocument.Parse("0").RootElement, null).ShouldBeFalse();
    }

    [Fact]
    public void CompareValues_treats_json_null_as_empty()
    {
        var nullEl = JsonDocument.Parse("null").RootElement;
        ConditionEvaluator.CompareValues("is_empty", nullEl, null).ShouldBeTrue();
        ConditionEvaluator.CompareValues("contains", nullEl, "x").ShouldBeFalse();
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseDict(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
