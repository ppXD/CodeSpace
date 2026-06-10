using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pure merge logic of <see cref="AgentDefinitionResolver"/> — how a persona's system prompt +
/// model fold into the node's inline values. The DB-load / not-found / team-scoping branches are covered
/// at the integration tier (they need a real persona row); this tier locks the precedence rules.
/// </summary>
[Trait("Category", "Unit")]
public class AgentDefinitionResolverTests
{
    [Fact]
    public void ComposeGoal_prepends_the_system_prompt_to_the_goal()
    {
        AgentDefinitionResolver.ComposeGoal("You are a reviewer.", "Review PR #42.")
            .ShouldBe("You are a reviewer.\n\nReview PR #42.");
    }

    [Theory]
    [InlineData("You are a reviewer.", "", "You are a reviewer.")]   // persona only (node goal empty)
    [InlineData("", "Review PR #42.", "Review PR #42.")]              // goal only (persona prompt empty)
    [InlineData("  spaced  ", "  goal  ", "spaced\n\ngoal")]          // both trimmed before joining
    [InlineData("", "", "")]                                         // nothing either side → empty (caller throws)
    public void ComposeGoal_handles_each_one_sided_case(string systemPrompt, string goal, string expected)
    {
        AgentDefinitionResolver.ComposeGoal(systemPrompt, goal).ShouldBe(expected);
    }

    [Theory]
    [InlineData("gpt-5.4", "gpt-5.3-codex", "gpt-5.4")]   // node override (non-blank) wins
    [InlineData("", "gpt-5.3-codex", "gpt-5.3-codex")]    // node blank → persona's model
    [InlineData("   ", "gpt-5.3-codex", "gpt-5.3-codex")] // node whitespace → persona's model
    [InlineData(null, "gpt-5.3-codex", "gpt-5.3-codex")]  // node absent → persona's model
    public void ResolveModel_prefers_a_non_blank_node_override_then_the_persona(string? nodeModel, string? personaModel, string expected)
    {
        AgentDefinitionResolver.ResolveModel(nodeModel, personaModel).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, null)]     // neither → null (harness default)
    [InlineData("", "")]         // both blank → null
    [InlineData("  ", null)]     // node whitespace, no persona model → null
    public void ResolveModel_is_null_when_neither_side_sets_a_model(string? nodeModel, string? personaModel)
    {
        AgentDefinitionResolver.ResolveModel(nodeModel, personaModel)
            .ShouldBeNull("no model anywhere = let the harness pick its own default (the Model=empty rule)");
    }

    [Fact]
    public void MergeTools_unions_persona_and_node_tools_order_stable_and_deduped()
    {
        AgentDefinitionResolver.MergeTools(new[] { "Read", "Grep" }, new[] { "Grep", "Bash" })
            .ShouldBe(new[] { "Read", "Grep", "Bash" }, "persona tools first, then node tools not already present — supplement, never narrow");
    }

    [Fact]
    public void MergeTools_returns_the_other_side_when_one_is_null_preserving_the_default_tristate()
    {
        // null = "inherit the harness default"; the present side wins as-is (no spurious empty list).
        AgentDefinitionResolver.MergeTools(null, new[] { "Bash" }).ShouldBe(new[] { "Bash" });
        AgentDefinitionResolver.MergeTools(new[] { "Read" }, null).ShouldBe(new[] { "Read" });
        AgentDefinitionResolver.MergeTools(null, null).ShouldBeNull("both inherit the default → still the default");
    }

    [Fact]
    public void MergeTools_skips_blank_entries()
    {
        AgentDefinitionResolver.MergeTools(new[] { "Read", "" }, new[] { "  ", "Bash" }).ShouldBe(new[] { "Read", "Bash" });
    }

    [Fact]
    public void MergeTools_of_two_empty_lists_is_empty_not_null()
    {
        // [] = "explicitly no tools" — the union of two empties stays empty (distinct from null = default).
        AgentDefinitionResolver.MergeTools(Array.Empty<string>(), Array.Empty<string>()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseTools_returns_null_for_absent_or_blank_json(string? toolsJson)
    {
        AgentDefinitionResolver.ParseTools(toolsJson).ShouldBeNull("null/blank ToolsJson = inherit the harness default");
    }

    [Fact]
    public void ParseTools_round_trips_a_json_array()
    {
        AgentDefinitionResolver.ParseTools("[\"Read\",\"Grep\"]").ShouldBe(new[] { "Read", "Grep" });
        AgentDefinitionResolver.ParseTools("[]").ShouldBeEmpty("an explicit empty array is 'no tools', not the default");
    }
}
