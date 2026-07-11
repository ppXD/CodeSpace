using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the supervisor's goal-config deserialization (<c>ReadGoalConfig</c>) is TOLERANT of the enum-as-string
/// values the editor stores (SchemaForm's {{ref}} unification). Regression for P0.2: a stray <c>"1"</c> for a review
/// mode used to throw a JsonException, and the catch dropped the ENTIRE goal config (goal, caps, approval, review).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorGoalConfigParseTests
{
    private static Dictionary<string, JsonElement> Bag(object obj) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(obj))!;

    [Fact]
    public void A_string_encoded_review_mode_is_read_and_the_whole_config_is_kept()
    {
        // exactly what the editor stores: enums + numbers as strings.
        var bag = Bag(new { goal = "ship it", decisionReviewMode = "1", maxTotalSpawns = "7" });

        var config = AgentSupervisorNode.ReadGoalConfig(bag);

        config.ShouldNotBeNull("a string-encoded enum must not drop the whole config");
        config!.Goal.ShouldBe("ship it");
        config.DecisionReviewMode.ShouldBe(ReviewMode.Gate);   // "1" → Gate, not dropped to None
        config.MaxTotalSpawns.ShouldBe(7);                     // "7" → 7
    }

    [Fact]
    public void A_plain_numeric_review_mode_still_reads_the_same()
    {
        var bag = Bag(new { goal = "ship it", decisionReviewMode = 1 });

        AgentSupervisorNode.ReadGoalConfig(bag)!.DecisionReviewMode.ShouldBe(ReviewMode.Gate);
    }

    [Fact]
    public void An_absent_review_mode_defaults_to_None()
    {
        var config = AgentSupervisorNode.ReadGoalConfig(Bag(new { goal = "ship it" }));

        config.ShouldNotBeNull();
        config!.DecisionReviewMode.ShouldBe(ReviewMode.None);
    }

    [Fact]
    public void The_primary_repository_is_read_from_the_nested_agent_profile()
    {
        // P0.3: the schema nests repositoryId under agentProfile (there is no flat top-level key), so the run's
        // repositoryId OUTPUT must be read from here — not a flat lookup that always yields null/empty.
        var repoId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var config = AgentSupervisorNode.ReadGoalConfig(Bag(new { goal = "ship it", agentProfile = new { repositoryId = repoId } }));

        config.ShouldNotBeNull();
        config!.AgentProfile.ShouldNotBeNull();
        config.AgentProfile!.RepositoryId.ShouldBe(repoId);
    }
}
