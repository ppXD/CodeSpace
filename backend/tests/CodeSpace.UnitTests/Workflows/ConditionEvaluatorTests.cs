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

    private static IReadOnlyDictionary<string, JsonElement> ParseDict(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
