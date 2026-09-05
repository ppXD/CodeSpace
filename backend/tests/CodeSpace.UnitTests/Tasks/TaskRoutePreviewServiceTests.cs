using System.Text.Json;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Tasks.Bounds;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Deep;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Quick;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Standard;
using CodeSpace.Core.Services.Tasks.Capabilities;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Launch;
using CodeSpace.Core.Services.Tasks.Launch.Providers.Chat;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.MapFanout;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Core.Services.Tasks.Recipes.Supervisor;
using CodeSpace.Core.Services.Tasks.RoutePreview;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// Pins the READ-ONLY route preview over the REAL production spine — the real chat seed provider, the real
/// effort router composing the real heuristic classifier / recipes / bounds presets. The load-bearing claims:
/// (a) the preview routes through the SAME request mapping the launch path uses, so what it shows is the route a
/// launch would actually take, and (b) the confirm card the router has always built for a low-confidence or
/// risky AUTO route is now REACHABLE before the run starts — an explicit tier still never confirms.
/// </summary>
[Trait("Category", "Unit")]
public class TaskRoutePreviewServiceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static IEffortRouter Router() => new EffortRouter(
        new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier() }),
        new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() }),
        new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset(), new DeepBoundsPreset() }),
        new CapabilityProbeRegistry(Array.Empty<ICapabilityProbe>()));

    private static TaskRoutePreviewService Preview(IEffortRouter router) => new(
        new TaskLaunchSeedProviderRegistry(new ITaskLaunchSeedProvider[] { new ChatSeedProvider() }),
        new AllRepositoriesInTeam(),
        router);

    private static TaskLaunchRequest Request(string goal, string? effort = null, string? recipe = null, RouteCaps? caps = null, string? shape = null) => new()
    {
        TeamId = Guid.NewGuid(),
        ActorUserId = Guid.NewGuid(),
        SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = goal,
        RequestedEffort = effort,
        RequestedRecipe = recipe,
        CapsOverride = caps,
        DeliverableShape = shape,
    };

    [Fact]
    public async Task Preview_routes_through_the_same_request_mapping_the_launch_path_uses()
    {
        // Every operator override the router consumes is set, so a preview that hand-rolled its own
        // EffortRouteRequest (and dropped one) diverges here rather than silently predicting a different run.
        var request = Request("Refactor the auth module across several files", effort: TaskEffortModes.Auto, recipe: TaskRecipeKinds.MapFanout, caps: new RouteCaps { MaxParallelism = 7, MaxCostUsd = 12.5m, AutonomyCeiling = "Confined" }, shape: DeliverableShapes.Document);

        var router = Router();

        var seed = await new ChatSeedProvider().SeedAsync(request, CancellationToken.None);
        var expected = await router.RouteAsync(TaskLaunchService.BuildRouteRequest(seed, request), CancellationToken.None);

        var previewed = (await Preview(router).PreviewAsync(request, CancellationToken.None)).Route;

        // Serialized comparison, not record equality: RouteCaps.Extra is a fresh dictionary per instance, so two
        // structurally identical plans are never Equals. The JSON is what crosses the wire anyway.
        JsonSerializer.Serialize(previewed, Json).ShouldBe(JsonSerializer.Serialize(expected, Json),
            customMessage: "the preview must route through TaskLaunchService.BuildRouteRequest — a divergence here means the composer is showing a route the launch would not take");
    }

    [Fact]
    public async Task An_explicit_tier_previews_the_shape_the_caller_carried_back()
    {
        // The composer echoes the shape a prior preview classified. On the explicit-tier path (a confirm-card answer)
        // the classifier never runs, so this carry is the ONLY thing that keeps the previewed route — and the launch it
        // predicts — from reverting an answer-shaped task to the coding projection.
        var previewed = (await Preview(Router()).PreviewAsync(Request("Explain how the retry loop works", effort: TaskEffortModes.Quick, shape: DeliverableShapes.Answer), CancellationToken.None)).Route;

        previewed.DeliverableShape.ShouldBe(DeliverableShapes.Answer);
    }

    [Fact]
    public async Task An_explicit_tier_previews_with_no_confirm_card()
    {
        var previewed = (await Preview(Router()).PreviewAsync(Request("Fix a small typo", effort: TaskEffortModes.Standard), CancellationToken.None)).Route;

        previewed.EffortMode.ShouldBe(TaskEffortModes.Standard);
        previewed.WasAutoClassified.ShouldBeFalse();
        previewed.NeedsConfirmCard.ShouldBeFalse("an operator who already chose a tier has nothing to confirm");
        previewed.Confirm.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]                     // an unset effort is the auto path
    [InlineData(TaskEffortModes.Auto)]     // …and so is the explicit "auto" sentinel
    public async Task An_auto_tier_previews_the_confirm_card_the_launch_would_otherwise_have_run_past(string? effort)
    {
        var previewed = (await Preview(Router()).PreviewAsync(Request("Deploy the migration to production", effort), CancellationToken.None)).Route;

        previewed.WasAutoClassified.ShouldBeTrue();
        previewed.NeedsConfirmCard.ShouldBeTrue("an auto route below the confidence floor is exactly what the operator must see BEFORE the run starts");
        previewed.Confirm.ShouldNotBeNull();
        previewed.Confirm!.SuggestedMode.ShouldBe(previewed.EffortMode);
        previewed.Confirm.Rationale.ShouldNotBeNullOrWhiteSpace("the card must say why — a tier with no reason is not a decision the operator can make");

        // The options are DERIVED from the bounds registry, so the composer's choices are the real available tiers.
        previewed.Confirm.Options.Select(o => o.Mode).ShouldBe(
            new[] { TaskEffortModes.Quick, TaskEffortModes.Standard, TaskEffortModes.Deep }, ignoreOrder: true);

        previewed.Decision!.Signals.RiskySideEffects.ShouldBeTrue("'deploy … production' is the risk signal the router escalates on regardless of model confidence");
    }

    /// <summary>A guard that accepts every repo — tenancy itself is proven against real Postgres in the integration tier; these tests pin the routing, not the query.</summary>
    private sealed class AllRepositoriesInTeam : ILaunchRepositoryScopeGuard
    {
        public Task EnsureInTeamAsync(TaskLaunchSeed seed, TaskLaunchRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
