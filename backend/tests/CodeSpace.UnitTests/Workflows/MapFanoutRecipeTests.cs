using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.MapFanout;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Core.Services.Tasks.Recipes.Supervisor;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the map-fanout recipe + the data-driven effort→recipe map on <see cref="TaskRecipeRegistry"/>. The recipe
/// declares its own field values (the plan→fan-out→synth shape, the plan-map-synth projection, the standard/deep
/// effort tiers it serves) and the registry resolves a recipe BY effort tier through <c>RecipeForEffort</c> with
/// NO hardcoded switch — plus the fail-fast no-overlap ctor assert that keeps the effort→recipe map a function.
/// </summary>
[Trait("Category", "Unit")]
public class MapFanoutRecipeTests
{
    private static readonly MapFanoutRecipe Recipe = new();

    [Fact]
    public void Reports_the_map_fanout_recipe_fields()
    {
        Recipe.RecipeKind.ShouldBe(TaskRecipeKinds.MapFanout);
        Recipe.DefaultProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth, "map-fanout builds the planner→map→synth graph");
        Recipe.BoundsPreset.ShouldBe(TaskEffortModes.Standard);
        Recipe.RecommendedAutonomy.ShouldBe("Standard");
        Recipe.RequiresPlanReview.ShouldBeFalse("the plan-review wait_approval gate variant is deferred to a later PR");
        Recipe.GoalFrame.ShouldBe("Decompose the task, work the subtasks in parallel, synthesize the results.");
        Recipe.RecommendedPhaseLabels.ShouldBe(new[] { "Plan", "Fan-out", "Synthesize" });
    }

    [Fact]
    public void Map_fanout_serves_standard_single_agent_serves_quick_supervisor_serves_deep()
    {
        new MapFanoutRecipe().ServesEfforts.ShouldBe(new[] { TaskEffortModes.Standard }, "PR6 moved deep to the supervisor recipe — map-fanout now serves standard only");
        new SingleAgentRecipe().ServesEfforts.ShouldBe(new[] { TaskEffortModes.Quick });
        new SupervisorRecipe().ServesEfforts.ShouldBe(new[] { TaskEffortModes.Deep });
    }

    [Theory]
    [InlineData(TaskEffortModes.Quick, TaskRecipeKinds.SingleAgent)]    // quick → single-agent
    [InlineData(TaskEffortModes.Standard, TaskRecipeKinds.MapFanout)]   // standard → map-fanout
    [InlineData(TaskEffortModes.Deep, TaskRecipeKinds.Supervisor)]      // deep → supervisor (PR6)
    public void RecipeForEffort_resolves_the_recipe_that_serves_the_tier(string effort, string expectedRecipeKind)
    {
        var registry = ProductionRegistry();

        registry.RecipeForEffort(effort).RecipeKind.ShouldBe(expectedRecipeKind);
    }

    [Fact]
    public void RecipeForEffort_falls_open_to_the_default_for_an_unserved_tier()
    {
        var registry = ProductionRegistry();

        registry.RecipeForEffort("some-unknown-tier").RecipeKind.ShouldBe(TaskRecipeKinds.SingleAgent,
            "a tier no recipe claims falls open to the default — never throws");
    }

    [Fact]
    public void Two_recipes_claiming_the_same_effort_tier_throw_in_the_ctor()
    {
        // Two recipes both serving "standard" — the registry must refuse to construct (the effort→recipe map
        // would otherwise be ambiguous), exactly like the duplicate-kind guard.
        var ctor = () => new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new OverlappingRecipe() });

        Should.Throw<InvalidOperationException>(ctor).Message.ShouldContain(TaskEffortModes.Standard);
    }

    private static TaskRecipeRegistry ProductionRegistry() =>
        new(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() });

    /// <summary>A test-only recipe that ALSO claims the 'standard' tier — used to prove the registry's no-overlap ctor assert fires.</summary>
    private sealed class OverlappingRecipe : ITaskRecipe
    {
        public string RecipeKind => "overlapping";
        public IReadOnlyList<string> ServesEfforts => new[] { TaskEffortModes.Standard };
        public string GoalFrame => "overlap";
        public string BoundsPreset => TaskEffortModes.Standard;
        public string RecommendedAutonomy => "Standard";
        public string DefaultProjectionKind => TaskProjectionKinds.SingleAgent;
        public bool RequiresPlanReview => false;
        public IReadOnlyList<string> RecommendedPhaseLabels => Array.Empty<string>();
        public string? RequiresCapability => null;
        public string? DegradesToRecipe => null;
    }
}
