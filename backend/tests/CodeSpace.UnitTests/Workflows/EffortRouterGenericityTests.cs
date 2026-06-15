using CodeSpace.Core.Services.Tasks.Bounds;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Quick;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Standard;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// THE zero-core-edit genericity proof for PR3. A brand-new classification / recipe / bounds STRATEGY is added
/// to each registry's <c>IEnumerable&lt;T&gt;</c> ONLY here (open kind strings the production core has never
/// heard of). The SAME <see cref="EffortRouter"/> — UNCHANGED — picks up the fake recipe's projection + the fake
/// bounds caps + (via the auto path) the fake classifier's decision. The router never names a concrete
/// classifier / recipe / preset type: every branch is a registry lookup. A new strategy is purely "register a
/// class", exactly like adding an IAgentHarness — proving zero production-core edit.
/// </summary>
[Trait("Category", "Unit")]
public class EffortRouterGenericityTests
{
    private const string FakeRecipeKind = "fake-recipe";
    private const string FakeBoundsKind = "fake-bounds";
    private const string FakeClassifierKind = "fake-classifier";

    private static EffortRouteRequest Request(string? requestedEffort = null, string? requestedRecipe = null) => new()
    {
        Seed = new TaskLaunchSeed { Goal = "route me", SurfaceKind = "test", TeamId = Guid.NewGuid() },
        RequestedEffort = requestedEffort,
        RequestedRecipe = requestedRecipe,
    };

    [Fact]
    public void Each_registry_lists_both_the_production_and_the_fake_kind()
    {
        var classifiers = new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier(), new FakeClassifier() });
        var recipes = new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new FakeRecipe() });
        var bounds = new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset(), new FakeBounds() });

        classifiers.All.Select(c => c.Kind).ShouldContain(HeuristicEffortClassifier.ClassifierKind, "the production classifier stays registered");
        classifiers.All.Select(c => c.Kind).ShouldContain(FakeClassifierKind, "the fake classifier joined the same axis additively");

        recipes.All.Select(r => r.RecipeKind).ShouldContain(TaskRecipeKinds.SingleAgent);
        recipes.All.Select(r => r.RecipeKind).ShouldContain(FakeRecipeKind);

        bounds.All.Select(b => b.PresetKind).ShouldContain(TaskEffortModes.Quick);
        bounds.All.Select(b => b.PresetKind).ShouldContain(FakeBoundsKind);
    }

    [Fact]
    public async Task Routing_a_request_pinned_to_the_fake_recipe_picks_up_its_projection_and_the_fake_bounds()
    {
        // The fake recipe defaults to a fake projection + the fake bounds preset; routing at the fake bounds tier
        // resolves the fake caps. The router is the UNCHANGED production class — zero core edit.
        var router = new EffortRouter(
            new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier(), new FakeClassifier() }),
            new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new FakeRecipe() }),
            new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset(), new FakeBounds() }));

        // RequestedEffort = the fake bounds kind so the effort-mode ≡ preset-kind convention resolves the fake caps;
        // RequestedRecipe = the fake recipe so its DefaultProjectionKind drives the projection.
        var plan = await router.RouteAsync(Request(requestedEffort: FakeBoundsKind, requestedRecipe: FakeRecipeKind), CancellationToken.None);

        plan.RecipeKind.ShouldBe(FakeRecipeKind, "the router resolved the fake recipe by its open kind string");
        plan.ProjectionKind.ShouldBe(FakeRecipe.FakeProjection, "the fake recipe's default projection flowed through with no core edit");
        plan.BoundsPreset.ShouldBe(FakeBoundsKind, "the fake bounds preset resolved by the effort mode");
        plan.Caps.MaxParallelism.ShouldBe(FakeBounds.DistinctiveParallelism, "the fake preset's distinctive caps reached the plan");
        plan.Caps.MaxRounds.ShouldBe(FakeBounds.DistinctiveRounds);
    }

    [Fact]
    public async Task Auto_path_routes_via_the_registry_Default_classifier_not_a_named_type()
    {
        // Proves the auto path is purely a registry.Default lookup, never a hardcoded concrete classifier: even
        // with an extra FAKE classifier registered alongside the heuristic, the router routes 'auto' through
        // whatever the registry reports as Default (the heuristic) — it never names a classifier type itself.
        var router = new EffortRouter(
            new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier(), new FakeClassifier() }),
            new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe() }),
            new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset() }));

        var plan = await router.RouteAsync(Request(), CancellationToken.None);

        plan.WasAutoClassified.ShouldBeTrue();
        plan.Decision!.ClassifierKind.ShouldBe(HeuristicEffortClassifier.ClassifierKind, "the router routed via the registry's Default classifier — never a named concrete type");
    }

    // ─── Fake strategies (production core has never heard of these kinds) ─────

    private sealed class FakeClassifier : IEffortClassifier
    {
        public string Kind => FakeClassifierKind;
        public Task<EffortDecision> ClassifyAsync(EffortRouteRequest request, CancellationToken ct) =>
            Task.FromResult(new EffortDecision
            {
                Signals = new EffortSignals(),
                SuggestedEffort = TaskEffortModes.Deep,
                SuggestedRecipe = FakeRecipeKind,   // suggests the FAKE recipe
                Confidence = 0.99,
                ClassifierKind = FakeClassifierKind,
            });
    }

    private sealed class FakeRecipe : ITaskRecipe
    {
        public const string FakeProjection = "fake-projection";
        public string RecipeKind => FakeRecipeKind;
        public string GoalFrame => "a fake recipe";
        public string BoundsPreset => FakeBoundsKind;
        public string RecommendedAutonomy => "Confined";
        public string DefaultProjectionKind => FakeProjection;
        public bool RequiresPlanReview => true;
        public IReadOnlyList<string> RecommendedPhaseLabels => new[] { "Fake phase" };
    }

    private sealed class FakeBounds : IBoundsPreset
    {
        public const int DistinctiveParallelism = 42;
        public const int DistinctiveRounds = 7;
        public string PresetKind => FakeBoundsKind;
        public RouteCaps ToCaps() => new() { MaxParallelism = DistinctiveParallelism, MaxRounds = DistinctiveRounds };
    }
}
