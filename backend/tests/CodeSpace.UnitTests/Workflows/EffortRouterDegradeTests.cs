using CodeSpace.Core.Services.Tasks.Bounds;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Quick;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Standard;
using CodeSpace.Core.Services.Tasks.Capabilities;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the GENERIC capability-availability DEGRADE on the FLAT router pipeline — DATA-DRIVEN, not hardcoded to any one
/// recipe: a FAKE recipe declaring a FAKE capability (reported unavailable) + a FAKE fallback degrades through the
/// UNCHANGED router with zero production-core knowledge of either string, and the DegradedReason is never silent. (The
/// supervisor lane — the only former real capability — graduated its feature gate and is always on, so its recipe no
/// longer degrades; the generic mechanism remains for a future capability that needs it.)
/// </summary>
[Trait("Category", "Unit")]
public class EffortRouterDegradeTests
{
    private static EffortRouteRequest Request(string requestedEffort) => new()
    {
        Seed = new TaskLaunchSeed { Goal = "ship the whole feature", SurfaceKind = "test", TeamId = Guid.NewGuid() },
        RequestedEffort = requestedEffort,
    };

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
