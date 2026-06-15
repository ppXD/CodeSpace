using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.MapFanout;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Core.Services.Tasks.Recipes.Supervisor;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the supervisor recipe's field values + the no-overlap invariant after PR6 moved <c>deep</c> from
/// map-fanout to supervisor. The recipe declares it serves <c>deep</c>, defaults to the supervisor projection,
/// and DECLARES its execution-time precondition (the supervisor-lane capability) + its degrade target
/// (map-fanout) — the data the router's degrade step reads. The production registry (single-agent + map-fanout +
/// supervisor) still constructs cleanly: each tier maps to at most one recipe (quick / standard / deep).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorRecipeTests
{
    private static readonly SupervisorRecipe Recipe = new();

    [Fact]
    public void Reports_the_supervisor_recipe_fields()
    {
        Recipe.RecipeKind.ShouldBe(TaskRecipeKinds.Supervisor);
        Recipe.ServesEfforts.ShouldBe(new[] { TaskEffortModes.Deep }, "PR6 — the supervisor recipe is the deep tier's shape");
        Recipe.DefaultProjectionKind.ShouldBe(TaskProjectionKinds.Supervisor, "supervisor builds the durable supervisor lane graph");
        Recipe.BoundsPreset.ShouldBe(TaskEffortModes.Deep);
        Recipe.RecommendedAutonomy.ShouldBe("Standard");
        Recipe.RequiresPlanReview.ShouldBeFalse();
        Recipe.RequiresCapability.ShouldBe(TaskCapabilities.SupervisorLane, "the supervisor projection needs the lane at execution");
        Recipe.DegradesToRecipe.ShouldBe(TaskRecipeKinds.MapFanout, "when the lane is off, deep degrades to the multi-agent map-fanout shape");
        Recipe.RecommendedPhaseLabels.ShouldBe(new[] { "Plan", "Delegate", "Synthesize" });
        Recipe.GoalFrame.ShouldBe("A supervisor plans, delegates to sub-agents in bounded rounds, and synthesizes — within the durable supervisor lane.");
    }

    [Fact]
    public void The_production_recipe_set_constructs_with_no_tier_overlap_and_resolves_deep_to_supervisor()
    {
        // single-agent=quick, map-fanout=standard, supervisor=deep — three disjoint tiers, the no-overlap ctor
        // assert is satisfied (it threw before map-fanout dropped 'deep' in PR6).
        var registry = new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() });

        registry.RecipeForEffort(TaskEffortModes.Quick).RecipeKind.ShouldBe(TaskRecipeKinds.SingleAgent);
        registry.RecipeForEffort(TaskEffortModes.Standard).RecipeKind.ShouldBe(TaskRecipeKinds.MapFanout);
        registry.RecipeForEffort(TaskEffortModes.Deep).RecipeKind.ShouldBe(TaskRecipeKinds.Supervisor);
    }

    [Fact]
    public void Two_recipes_both_claiming_deep_throw_in_the_ctor()
    {
        // The no-overlap invariant the PR6 deep move depends on: if BOTH map-fanout (pre-PR6) and supervisor
        // claimed 'deep', the effort→recipe map would be ambiguous and the registry must refuse to construct.
        var ctor = () => new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new SupervisorRecipe(), new DeepClaimingRecipe() });

        Should.Throw<InvalidOperationException>(ctor).Message.ShouldContain(TaskEffortModes.Deep);
    }

    /// <summary>A test-only recipe that ALSO claims the 'deep' tier — proves the registry's no-overlap ctor assert still guards the tier the supervisor recipe now owns.</summary>
    private sealed class DeepClaimingRecipe : ITaskRecipe
    {
        public string RecipeKind => "deep-claiming";
        public IReadOnlyList<string> ServesEfforts => new[] { TaskEffortModes.Deep };
        public string GoalFrame => "overlap";
        public string BoundsPreset => TaskEffortModes.Deep;
        public string RecommendedAutonomy => "Standard";
        public string DefaultProjectionKind => TaskProjectionKinds.SingleAgent;
        public bool RequiresPlanReview => false;
        public IReadOnlyList<string> RecommendedPhaseLabels => Array.Empty<string>();
        public string? RequiresCapability => null;
        public string? DegradesToRecipe => null;
    }
}
