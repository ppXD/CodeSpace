using CodeSpace.Core.Services.Tasks.Bounds;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Deep;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Quick;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Standard;
using CodeSpace.Core.Services.Tasks.Capabilities;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.MapFanout;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Core.Services.Tasks.Recipes.Supervisor;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the PR6 lane-availability DEGRADE on the FLAT router pipeline. With the production recipe set
/// (single-agent / map-fanout / supervisor) and a FAKE capability-probe registry reporting the supervisor-lane
/// capability available or unavailable, an explicit <c>deep</c> request either reaches the supervisor recipe
/// (lane on → null DegradedReason) or degrades to map-fanout (lane off → a NON-NULL DegradedReason — never
/// silent). The GENERICITY contract test proves the degrade is DATA-DRIVEN, not supervisor-hardcoded: a fake
/// recipe declaring a fake capability + a fake fallback degrades through the UNCHANGED router with zero
/// production-core knowledge of either string.
/// </summary>
[Trait("Category", "Unit")]
public class EffortRouterDegradeTests
{
    private static EffortRouter Router(bool laneAvailable, params ITaskRecipe[] extraRecipes)
    {
        var recipes = new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() }.Concat(extraRecipes).ToArray();

        return new EffortRouter(
            new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier() }),
            new TaskRecipeRegistry(recipes),
            new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset(), new DeepBoundsPreset() }),
            new CapabilityProbeRegistry(new ICapabilityProbe[] { new FakeProbe(TaskCapabilities.SupervisorLane, laneAvailable) }));
    }

    private static EffortRouteRequest Request(string requestedEffort) => new()
    {
        Seed = new TaskLaunchSeed { Goal = "ship the whole feature", SurfaceKind = "test", TeamId = Guid.NewGuid() },
        RequestedEffort = requestedEffort,
    };

    [Fact]
    public async Task Deep_with_the_lane_available_routes_the_supervisor_recipe_with_no_degrade()
    {
        var plan = await Router(laneAvailable: true).RouteAsync(Request(TaskEffortModes.Deep), CancellationToken.None);

        plan.RecipeKind.ShouldBe(TaskRecipeKinds.Supervisor);
        plan.ProjectionKind.ShouldBe(TaskProjectionKinds.Supervisor);
        plan.DegradedReason.ShouldBeNull("the lane is available, so the supervisor recipe routes unchanged");
    }

    [Fact]
    public async Task Deep_with_the_lane_unavailable_degrades_to_map_fanout_with_a_non_null_reason()
    {
        var plan = await Router(laneAvailable: false).RouteAsync(Request(TaskEffortModes.Deep), CancellationToken.None);

        plan.RecipeKind.ShouldBe(TaskRecipeKinds.MapFanout, "deep degrades to the map-fanout shape when the supervisor lane is off");
        plan.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth, "the projection recomputes from the fallback recipe's default");
        plan.DegradedReason.ShouldNotBeNullOrEmpty("a degrade is NEVER silent — DegradedReason is always set when it fires");
        plan.DegradedReason!.ShouldContain(TaskRecipeKinds.Supervisor);
        plan.DegradedReason.ShouldContain(TaskCapabilities.SupervisorLane);
        plan.DegradedReason.ShouldContain(TaskRecipeKinds.MapFanout);
    }

    [Fact]
    public async Task A_non_degrading_tier_keeps_a_null_reason()
    {
        // standard → map-fanout has no required capability, so it never degrades regardless of the lane state.
        var plan = await Router(laneAvailable: false).RouteAsync(Request(TaskEffortModes.Standard), CancellationToken.None);

        plan.RecipeKind.ShouldBe(TaskRecipeKinds.MapFanout);
        plan.DegradedReason.ShouldBeNull("a recipe with no required capability never degrades");
    }

    [Fact]
    public async Task The_degrade_is_data_driven_a_fake_recipe_and_fake_capability_degrade_with_zero_router_edit()
    {
        // THE genericity proof: a FAKE recipe declares it needs a FAKE capability (reported unavailable) + a
        // FAKE fallback (single-agent), all open strings the production router has never heard of. The SAME
        // EffortRouter — UNCHANGED — degrades to single-agent + sets a DegradedReason, with ZERO production-core
        // knowledge of either string. The router never names 'supervisor' / 'supervisor-lane' — it reads only
        // what the recipe itself declares + asks the probe registry.
        var router = new EffortRouter(
            new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier() }),
            new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new FakeGatedRecipe() }),
            new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset() }),
            new CapabilityProbeRegistry(new ICapabilityProbe[] { new FakeProbe(FakeGatedRecipe.FakeCapability, available: false) }));

        var plan = await router.RouteAsync(Request(FakeGatedRecipe.FakeTier), CancellationToken.None);

        plan.RecipeKind.ShouldBe(TaskRecipeKinds.SingleAgent, "the fake recipe degraded to its declared fallback — purely data-driven");
        plan.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent, "the projection recomputed from the fallback recipe's default");
        plan.DegradedReason.ShouldNotBeNullOrEmpty();
        plan.DegradedReason!.ShouldContain(FakeGatedRecipe.FakeCapability, customMessage: "the reason names the fake capability the production core never knew");
    }

    private sealed class FakeProbe : ICapabilityProbe
    {
        private readonly bool _available;
        public FakeProbe(string capability, bool available) { Capability = capability; _available = available; }
        public string Capability { get; }
        public bool IsAvailable() => _available;
    }

    /// <summary>A fake recipe declaring a fake capability + a fake fallback — production core has never heard of either string.</summary>
    private sealed class FakeGatedRecipe : ITaskRecipe
    {
        public const string FakeTier = "fake-tier";
        public const string FakeCapability = "fake-cap";
        public string RecipeKind => "fake-recipe";
        public IReadOnlyList<string> ServesEfforts => new[] { FakeTier };
        public string GoalFrame => "a fake gated recipe";
        public string BoundsPreset => TaskEffortModes.Standard;
        public string RecommendedAutonomy => "Standard";
        public string DefaultProjectionKind => "fake-projection";
        public bool RequiresPlanReview => false;
        public IReadOnlyList<string> RecommendedPhaseLabels => Array.Empty<string>();
        public string? RequiresCapability => FakeCapability;
        public string? DegradesToRecipe => TaskRecipeKinds.SingleAgent;
    }
}
