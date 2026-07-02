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
    public void ResolvePersonaPrompt_keeps_the_persona_in_the_system_channel_and_the_goal_clean()
    {
        // B1: the persona rides SystemPrompt (its own native channel), NOT prepended to the goal.
        var (goal, systemPrompt) = AgentDefinitionResolver.ResolvePersonaPrompt("You are a reviewer.", "Review PR #42.");

        goal.ShouldBe("Review PR #42.", "the goal stays the CLEAN task — no persona baked in");
        systemPrompt.ShouldBe("You are a reviewer.", "the persona goes to the system-prompt channel");
    }

    [Theory]
    [InlineData("You are a reviewer.", "", "You are a reviewer.", null)]   // persona-only (no node goal) → persona STAYS the goal (byte-identical to pre-B1); no separate system prompt
    [InlineData("", "Review PR #42.", "Review PR #42.", null)]             // goal-only (no persona) → clean goal, null system prompt
    [InlineData("  spaced  ", "  goal  ", "goal", "spaced")]               // both trimmed; persona → system, goal → user
    [InlineData("", "", "", null)]                                        // nothing either side → empty goal (caller throws)
    public void ResolvePersonaPrompt_handles_each_one_sided_case(string systemPrompt, string goal, string expectedGoal, string? expectedSystem)
    {
        var (resolvedGoal, resolvedSystem) = AgentDefinitionResolver.ResolvePersonaPrompt(systemPrompt, goal);

        resolvedGoal.ShouldBe(expectedGoal);
        resolvedSystem.ShouldBe(expectedSystem);
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
    public void ResolveModelCredentialId_prefers_the_node_ref_then_the_persona()
    {
        var node = Guid.NewGuid();
        var persona = Guid.NewGuid();

        AgentDefinitionResolver.ResolveModelCredentialId(node, persona).ShouldBe(node);    // node-pinned override wins
        AgentDefinitionResolver.ResolveModelCredentialId(null, persona).ShouldBe(persona); // node absent → persona default
        AgentDefinitionResolver.ResolveModelCredentialId(null, null)
            .ShouldBeNull("neither side pins one = fall back to a team/operator key at resolve time");
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

    [Fact]
    public void ParseTools_wraps_a_corrupt_blob_as_a_clean_resolution_failure() =>
        // A corrupt imported ToolsJson must surface as the typed exception the engine maps to a clean node
        // failure — not a raw JsonException escaping the resolve path.
        Should.Throw<AgentDefinitionResolutionException>(() => AgentDefinitionResolver.ParseTools("{not-an-array"))
            .Message.ShouldContain("unreadable tools list");
}
