using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.MapFanout;
using CodeSpace.Core.Services.Tasks.Recipes.MapFanoutDynamic;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Core.Services.Tasks.Recipes.Supervisor;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the map-fanout-dynamic recipe — the MODEL-AUTHORED sibling of map-fanout. It declares the plan-map-dynamic
/// projection and is OPT-IN (<see cref="MapFanoutDynamicRecipe.ServesEfforts"/> empty), so it claims no effort tier
/// and is reached only by an explicit <c>RequestedRecipe</c> — which keeps the registry's no-overlap assert happy
/// and leaves the standard→map-fanout tier routing byte-identical.
/// </summary>
[Trait("Category", "Unit")]
public class MapFanoutDynamicRecipeTests
{
    private static readonly MapFanoutDynamicRecipe Recipe = new();

    [Fact]
    public void Reports_the_map_fanout_dynamic_recipe_fields()
    {
        Recipe.RecipeKind.ShouldBe(TaskRecipeKinds.MapFanoutDynamic);
        Recipe.DefaultProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapDynamic, "map-fanout-dynamic builds the model-authored planner→map→synth graph");
        Recipe.BoundsPreset.ShouldBe(TaskEffortModes.Standard);
        Recipe.RecommendedAutonomy.ShouldBe("Standard");
        Recipe.RequiresPlanReview.ShouldBeFalse("like map-fanout it emits no PR-open node — merge stays human-gated, no plan-review gate");
        Recipe.RecommendedPhaseLabels.ShouldBe(new[] { "Plan", "Fan-out", "Synthesize" });
        Recipe.RequiresCapability.ShouldBeNull("the dynamic projection needs no execution-time capability");
        Recipe.DegradesToRecipe.ShouldBeNull();
    }

    [Fact]
    public void Serves_no_effort_tier_so_it_is_opt_in_only()
    {
        Recipe.ServesEfforts.ShouldBeEmpty("map-fanout-dynamic is opt-in — it claims no tier, reached only by an explicit RequestedRecipe (so standard stays mapped to map-fanout)");
    }

    [Fact]
    public void Registers_alongside_the_other_recipes_without_contesting_a_tier()
    {
        // The opt-in recipe (ServesEfforts=[]) must NOT trip the registry's no-overlap ctor assert, and the
        // existing tier routing must be unchanged — standard still resolves to map-fanout.
        var registry = new TaskRecipeRegistry(new ITaskRecipe[]
        {
            new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe(), new MapFanoutDynamicRecipe(),
        });

        registry.RecipeForEffort(TaskEffortModes.Standard).RecipeKind.ShouldBe(TaskRecipeKinds.MapFanout,
            "the opt-in dynamic recipe claims no tier, so standard stays mapped to map-fanout — no tier reroute");
    }
}
