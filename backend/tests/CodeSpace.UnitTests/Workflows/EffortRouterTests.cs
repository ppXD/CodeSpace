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
/// Pins the L2 effort router over the REAL production registries (the heuristic classifier, the single-agent /
/// map-fanout / supervisor recipes, the three bounds presets) — the FLAT pipeline that turns a request into a
/// RoutePlan. Covers the non-auto operator path (no classifier, no confirm card, the requested tier + its caps),
/// the auto path (the heuristic always-confirms, with confirm options DERIVED from the bounds registry), the
/// RequestedProjection escape hatch, and the CapsOverride merge. The supervisor-lane DEGRADE pins (lane on →
/// supervisor, lane off → map-fanout + DegradedReason) live in <see cref="EffortRouterDegradeTests"/>; the
/// GENERICITY contract test lives in <see cref="EffortRouterGenericityTests"/>. The supervisor-lane capability
/// is reported AVAILABLE here (a fixed probe) so an explicit <c>deep</c> reaches the supervisor recipe
/// deterministically, independent of the ambient env flag.
/// </summary>
[Trait("Category", "Unit")]
public class EffortRouterTests
{
    private static EffortRouter Router() => new(
        new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier() }),
        new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() }),
        new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset(), new DeepBoundsPreset() }),
        new CapabilityProbeRegistry(new ICapabilityProbe[] { new FixedProbe(TaskCapabilities.SupervisorLane, available: true) }));

    private static EffortRouteRequest Request(string goal, string? requestedEffort = null, string? requestedRecipe = null, string? requestedProjection = null, RouteCaps? capsOverride = null) => new()
    {
        Seed = new TaskLaunchSeed { Goal = goal, SurfaceKind = "test", TeamId = Guid.NewGuid() },
        RequestedEffort = requestedEffort,
        RequestedRecipe = requestedRecipe,
        RequestedProjection = requestedProjection,
        CapsOverride = capsOverride,
    };

    [Fact]
    public async Task Non_auto_request_honours_the_tier_with_no_classifier_and_no_confirm_card()
    {
        var plan = await Router().RouteAsync(Request("anything at all", requestedEffort: TaskEffortModes.Standard), CancellationToken.None);

        plan.EffortMode.ShouldBe(TaskEffortModes.Standard);
        plan.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth, "explicit 'standard' routes the map-fanout recipe's default projection");
        plan.RecipeKind.ShouldBe(TaskRecipeKinds.MapFanout, "explicit 'standard' is served by the map-fanout recipe");
        plan.BoundsPreset.ShouldBe(TaskEffortModes.Standard);

        plan.WasAutoClassified.ShouldBeFalse();
        plan.NeedsConfirmCard.ShouldBeFalse("an explicit operator tier never asks for confirmation");
        plan.Confirm.ShouldBeNull();
        plan.ClassifierConfidence.ShouldBe(1.0, "an operator decision is full-confidence");
        plan.Decision!.ClassifierKind.ShouldBe("operator");

        // The standard preset's caps flowed onto the plan.
        plan.Caps.MaxParallelism.ShouldBe(3);
        plan.Caps.MaxRounds.ShouldBe(3);
        plan.Caps.MaxTotalSpawns.ShouldBe(8);
    }

    [Fact]
    public async Task Auto_request_classifies_and_always_asks_to_confirm_with_options_derived_from_the_bounds_registry()
    {
        var plan = await Router().RouteAsync(Request("Fix a small typo in the docs"), CancellationToken.None);

        plan.WasAutoClassified.ShouldBeTrue();
        plan.NeedsConfirmCard.ShouldBeTrue("the heuristic is always below the confirm floor, so the auto path always confirms");
        plan.ClassifierConfidence.ShouldBeLessThan(EffortPolicy.ConfirmConfidenceFloor);

        plan.Confirm.ShouldNotBeNull();
        plan.Confirm!.SuggestedMode.ShouldBe(plan.EffortMode);

        // The options are DERIVED from the bounds registry — one per available preset, not a hardcoded list.
        plan.Confirm.Options.Select(o => o.Mode).ShouldBe(
            new[] { TaskEffortModes.Quick, TaskEffortModes.Standard, TaskEffortModes.Deep }, ignoreOrder: true);
    }

    [Fact]
    public async Task Auto_request_re_entered_with_the_chosen_tier_short_circuits_the_classifier()
    {
        // The operator's answer to the confirm card re-enters as RequestedEffort and routes deterministically.
        var plan = await Router().RouteAsync(Request("Fix a small typo in the docs", requestedEffort: TaskEffortModes.Deep), CancellationToken.None);

        plan.EffortMode.ShouldBe(TaskEffortModes.Deep);
        plan.WasAutoClassified.ShouldBeFalse();
        plan.NeedsConfirmCard.ShouldBeFalse();
        plan.Caps.MaxTotalSpawns.ShouldBe(20, "the deep preset's caps");
    }

    [Fact]
    public async Task RequestedProjection_overrides_the_recipe_default_projection()
    {
        var plan = await Router().RouteAsync(Request("x", requestedEffort: TaskEffortModes.Quick, requestedProjection: "some-future-projection"), CancellationToken.None);

        plan.ProjectionKind.ShouldBe("some-future-projection", "the escape hatch pins the projection regardless of the recipe");
        plan.RecipeKind.ShouldBe(TaskRecipeKinds.SingleAgent);
    }

    [Fact]
    public async Task CapsOverride_merges_set_fields_over_the_preset_caps()
    {
        var overrideCaps = new RouteCaps { MaxParallelism = 2, RequiresApproval = true };

        var plan = await Router().RouteAsync(Request("x", requestedEffort: TaskEffortModes.Standard, capsOverride: overrideCaps), CancellationToken.None);

        plan.Caps.MaxParallelism.ShouldBe(2, "the set override field wins over the preset's 3");
        plan.Caps.MaxRounds.ShouldBe(3, "an unset override field keeps the preset's value");
        plan.Caps.MaxTotalSpawns.ShouldBe(8, "an unset override field keeps the preset's value");
        plan.Caps.RequiresApproval.ShouldBeTrue("the override tightened approval on");
    }

    [Fact]
    public async Task Unknown_requested_recipe_fails_open_to_the_default_recipe_without_throwing()
    {
        var plan = await Router().RouteAsync(Request("x", requestedEffort: TaskEffortModes.Quick, requestedRecipe: "no-such-recipe"), CancellationToken.None);

        plan.RecipeKind.ShouldBe(TaskRecipeKinds.SingleAgent, "an unknown recipe fails open to the safe default — never throws");
        plan.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);
    }

    // ─── THE effort-tier pins: an explicit effort tier (no requested recipe) → the recipe that SERVES it ───

    [Fact]
    public async Task Explicit_standard_routes_the_map_fanout_recipe_and_plan_map_synth_projection()
    {
        var plan = await Router().RouteAsync(Request("Improve onboarding", requestedEffort: TaskEffortModes.Standard), CancellationToken.None);

        plan.RecipeKind.ShouldBe(TaskRecipeKinds.MapFanout, "explicit 'standard' is served by the map-fanout recipe");
        plan.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth, "the map-fanout recipe's default projection is the planner→map→synth graph");
        plan.NeedsConfirmCard.ShouldBeFalse("an explicit operator tier never confirms");
    }

    [Fact]
    public async Task Explicit_deep_routes_the_supervisor_recipe_when_the_lane_is_available()
    {
        // PR6: deep now routes the supervisor recipe (the lane capability is reported available here). The
        // lane-off degrade back to map-fanout is pinned in EffortRouterDegradeTests.
        var plan = await Router().RouteAsync(Request("Ship the whole feature", requestedEffort: TaskEffortModes.Deep), CancellationToken.None);

        plan.RecipeKind.ShouldBe(TaskRecipeKinds.Supervisor, "explicit 'deep' is served by the supervisor recipe");
        plan.ProjectionKind.ShouldBe(TaskProjectionKinds.Supervisor, "the supervisor recipe's default projection is the durable supervisor lane");
        plan.DegradedReason.ShouldBeNull("the lane is available, so no degrade fired");
        plan.NeedsConfirmCard.ShouldBeFalse("an explicit operator tier never confirms");
    }

    [Fact]
    public async Task Explicit_quick_stays_single_agent()
    {
        var plan = await Router().RouteAsync(Request("Fix a typo", requestedEffort: TaskEffortModes.Quick), CancellationToken.None);

        plan.RecipeKind.ShouldBe(TaskRecipeKinds.SingleAgent, "explicit 'quick' is served by the single-agent recipe");
        plan.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);
    }

    [Fact]
    public async Task Auto_path_still_suggests_single_agent_and_confirms_even_with_map_fanout_registered()
    {
        // The heuristic baseline stays conservative: it suggests single-agent + always asks the operator to
        // confirm. Escalation to map-fanout happens only when the operator picks standard/deep in the confirm
        // card, which re-enters as an EXPLICIT tier (the cases above).
        var plan = await Router().RouteAsync(Request("Refactor the auth module across files and add tests"), CancellationToken.None);

        plan.WasAutoClassified.ShouldBeTrue();
        plan.NeedsConfirmCard.ShouldBeTrue("the auto path always confirms — it never silently escalates to map-fanout");
        plan.RecipeKind.ShouldBe(TaskRecipeKinds.SingleAgent, "the heuristic suggests the conservative single-agent recipe");
        plan.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);
    }

    /// <summary>A test probe reporting a fixed availability for one capability — lets the router pin reach the supervisor recipe deterministically, independent of the ambient env flag.</summary>
    private sealed class FixedProbe : ICapabilityProbe
    {
        private readonly bool _available;
        public FixedProbe(string capability, bool available) { Capability = capability; _available = available; }
        public string Capability { get; }
        public bool IsAvailable() => _available;
    }
}
