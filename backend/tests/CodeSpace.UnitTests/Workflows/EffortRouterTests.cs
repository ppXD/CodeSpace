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
/// RequestedProjection escape hatch, and the CapsOverride merge. The supervisor lane is always on (its feature gate
/// graduated), so an explicit <c>deep</c> reaches the supervisor recipe with no capability probe + no degrade; the
/// generic capability-degrade mechanism is pinned in <see cref="EffortRouterDegradeTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class EffortRouterTests
{
    private static EffortRouter Router() => new(
        new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier() }),
        new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() }),
        new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset(), new DeepBoundsPreset() }),
        new CapabilityProbeRegistry(Array.Empty<ICapabilityProbe>()));

    private static EffortRouteRequest Request(string goal, string? requestedEffort = null, string? requestedRecipe = null, string? requestedProjection = null, RouteCaps? capsOverride = null, string? deliverableShape = null) => new()
    {
        Seed = new TaskLaunchSeed { Goal = goal, SurfaceKind = "test", TeamId = Guid.NewGuid() },
        RequestedEffort = requestedEffort,
        RequestedRecipe = requestedRecipe,
        RequestedProjection = requestedProjection,
        CapsOverride = capsOverride,
        DeliverableShape = deliverableShape,
    };

    [Theory]
    [InlineData(DeliverableShapes.Answer)]
    [InlineData(DeliverableShapes.Document)]
    [InlineData(DeliverableShapes.Research)]
    public async Task An_explicit_tier_keeps_the_shape_the_caller_carried_back(string shape)
    {
        // The refutation: the heuristic lane ALWAYS raises a confirm card, and the operator's answer re-enters as an
        // explicit tier — the path that skips the classifier. Without the carry, every confirmed launch reverted to
        // the coding projection, so the shape axis could never reach a run on the lane that needs it most.
        var plan = await Router().RouteAsync(Request("Explain how the retry loop works", requestedEffort: TaskEffortModes.Quick, deliverableShape: shape), CancellationToken.None);

        plan.WasAutoClassified.ShouldBeFalse("an explicit tier still short-circuits the classifier — only the shape rides along");
        plan.DeliverableShape.ShouldBe(shape,
            customMessage: "the operator confirmed a TIER, not a change of shape — the shape the card was raised about must survive the round trip");
        plan.Decision!.Signals.DeliverableShape.ShouldBe(shape, "the decision's own signals carry it too, so anything reading the decision sees the same shape");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a-shape-nobody-has-heard-of")]
    public async Task An_explicit_tier_with_no_usable_carried_shape_stays_code(string? carried)
    {
        // Byte-identical fall-back: nothing carried (an older client, a surface with no preview) and an unknown value
        // both read as the historical coding assumption rather than disarming the projection.
        var plan = await Router().RouteAsync(Request("Fix the failing login test", requestedEffort: TaskEffortModes.Quick, deliverableShape: carried), CancellationToken.None);

        plan.DeliverableShape.ShouldBe(DeliverableShapes.Code);
        plan.Decision!.Signals.ShouldBe(carried is null or "" or "   " ? new EffortSignals() : new EffortSignals { DeliverableShape = DeliverableShapes.Code });
    }

    [Fact]
    public async Task A_carried_shape_never_overrides_the_classifier_on_the_auto_path()
    {
        // The carry exists for the classifier-LESS path only. On auto the classifier reads the task itself, and a
        // stale echo from an earlier goal must not be able to overrule what it just read.
        var plan = await Router().RouteAsync(Request("Fix the failing login test", requestedEffort: TaskEffortModes.Auto, deliverableShape: DeliverableShapes.Answer), CancellationToken.None);

        plan.WasAutoClassified.ShouldBeTrue();
        plan.DeliverableShape.ShouldBe(DeliverableShapes.Code, "the classifier read a coding task — a carried shape is an echo, never an override");
    }

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

        // The standard preset's caps flowed onto the plan — the tier tunes ONLY concurrency now; round / total-spawn are
        // no longer tier knobs (a supervised run loops until done, bounded by cost / no-progress / the model's stop).
        plan.Caps.MaxParallelism.ShouldBe(3);
        plan.Caps.MaxTotalSpawns.ShouldBeNull("the tier no longer caps total spawns — concurrency is the only agent knob");
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
        plan.Caps.MaxParallelism.ShouldBe(5, "the deep preset's caps — wide concurrency");
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
        plan.Caps.MaxTotalSpawns.ShouldBeNull("an unset override field keeps the preset's value — the preset no longer sets total spawns");
        plan.Caps.RequiresApproval.ShouldBeTrue("the override tightened approval on");
    }

    [Fact]
    public async Task CapsOverride_autonomy_ceiling_tightens_only_never_escalates()
    {
        // A looser override ceiling (Unleashed) on a Standard route must NOT raise the ceiling — the privilege
        // bound stays un-bypassable; a stricter override (Confined) lowers it.
        var loosen = await Router().RouteAsync(Request("x", requestedEffort: TaskEffortModes.Standard, capsOverride: new RouteCaps { AutonomyCeiling = "Unleashed" }), CancellationToken.None);
        loosen.Caps.AutonomyCeiling.ShouldBe("Trusted", "an override can never RAISE the autonomy ceiling above the preset — it stops at Standard's own Trusted, never Unleashed");

        var tighten = await Router().RouteAsync(Request("x", requestedEffort: TaskEffortModes.Standard, capsOverride: new RouteCaps { AutonomyCeiling = "Confined" }), CancellationToken.None);
        tighten.Caps.AutonomyCeiling.ShouldBe("Confined", "an override may LOWER the ceiling to a stricter tier");
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
}
