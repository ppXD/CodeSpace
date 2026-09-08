using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Completion;
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
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Tasks.Projection.Builders.SingleAgent;
using CodeSpace.Core.Services.Tasks.Projection.Builders.Supervisor;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapSynth;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// Pins the READ-ONLY route preview over the REAL production spine — the real chat seed provider, the real
/// effort router composing the real heuristic classifier / recipes / bounds presets. The load-bearing claims:
/// (a) the preview routes through the SAME request mapping the launch path uses, so what it shows is the route a
/// launch would actually take, (b) the confirm card the router has always built for a low-confidence or
/// risky AUTO route is now REACHABLE before the run starts — an explicit tier still never confirms, and (c, arc3
/// item 3.2) the reported <see cref="TaskRoutePosture"/> is not a parallel guess — it calls the SAME
/// <see cref="TaskLaunchService.BuildAgentProfile"/> / <see cref="AgentAutonomyPolicy.DescribeNetwork"/> /
/// <see cref="CompletionPolicy.StampModeFor"/> the launch itself calls.
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
        new RoutingOnlySnapshotStore(router), new TaskProjectionRegistry([new SingleAgentDefinitionBuilder(), new SupervisorDefinitionBuilder(), new PlanMapSynthDefinitionBuilder()]),
        new ModeProfileRegistry());

    private static TaskLaunchRequest Request(string goal, string? effort = null, string? recipe = null, RouteCaps? caps = null, string? shape = null, string? autonomy = null, string? completionMode = null) => new()
    {
        TeamId = Guid.NewGuid(),
        ActorUserId = Guid.NewGuid(),
        SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = goal,
        RequestedEffort = effort,
        RequestedRecipe = recipe,
        CapsOverride = caps,
        DeliverableShape = shape,
        Autonomy = autonomy,
        CompletionMode = completionMode,
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

    [Theory]
    [InlineData(TaskEffortModes.Quick, true, TaskAcceptanceCompatibilityState.Compatible)]
    [InlineData(TaskEffortModes.Quick, false, TaskAcceptanceCompatibilityState.Incompatible)]
    [InlineData(TaskEffortModes.Deep, true, TaskAcceptanceCompatibilityState.Compatible)]
    [InlineData(TaskEffortModes.Deep, false, TaskAcceptanceCompatibilityState.Incompatible)]
    [InlineData(TaskEffortModes.Standard, true, TaskAcceptanceCompatibilityState.Incompatible)]
    public async Task Adapter_compatibility_comes_from_the_actual_builder_and_workspace(string effort, bool withRepository, TaskAcceptanceCompatibilityState expected)
    {
        var preview = await Preview(Router()).PreviewAsync(Request("Validate the report", effort) with { RepositoryId = withRepository ? Guid.NewGuid() : null }, CancellationToken.None);
        preview.AcceptanceCompatibility.ShouldNotBeNull();
        preview.AcceptanceCompatibility.State.ShouldBe(expected);
        preview.AcceptanceCompatibility.ProjectionKind.ShouldBe(preview.Route.ProjectionKind);
        if (effort != TaskEffortModes.Standard)
        {
            preview.AcceptanceCompatibility.GradingKind.ShouldBe("TestsPass");
            preview.AcceptanceCompatibility.RequiresRepository.ShouldBe(true);
        }
        preview.Route.WasAutoClassified.ShouldBeFalse("asking about an explicit route's adapter must not classify the goal");
    }

    [Fact]
    public async Task Unadvertised_projection_compatibility_stays_unknown_without_changing_the_route_identity()
    {
        var request = Request("Inspect this task", TaskEffortModes.Quick) with { RepositoryId = Guid.NewGuid() };
        var service = new TaskRoutePreviewService(new TaskLaunchSeedProviderRegistry([new ChatSeedProvider()]), new AllRepositoriesInTeam(), new RoutingOnlySnapshotStore(Router()), new TaskProjectionRegistry([]), new ModeProfileRegistry());
        var unknown = await service.PreviewAsync(request, CancellationToken.None);
        var known = await Preview(Router()).PreviewAsync(request, CancellationToken.None);
        unknown.AcceptanceCompatibility!.State.ShouldBe(TaskAcceptanceCompatibilityState.Unknown);
        JsonSerializer.Serialize(unknown.Route, Json).ShouldBe(JsonSerializer.Serialize(known.Route, Json));
    }

    // ─── Posture (arc3 item 3.2): "preview is not the run" ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(TaskEffortModes.Quick, null, "Standard", false, "Network: clamped off by policy (ceiling Standard)")]
    [InlineData(TaskEffortModes.Standard, null, "Standard", false, "Network: off (Standard)")]
    [InlineData(TaskEffortModes.Standard, "Trusted", "Trusted", true, "Network: on (Trusted)")]
    [InlineData(TaskEffortModes.Deep, null, "Standard", false, "Network: off (Standard)")]
    [InlineData(TaskEffortModes.Deep, "Trusted", "Trusted", true, "Network: on (Trusted)")]
    public async Task Posture_states_the_resolved_autonomy_and_network_the_launch_would_get(string effort, string? requestedAutonomy, string expectedAutonomy, bool expectedNetworkOn, string expectedNetworkPrefix)
    {
        var preview = await Preview(Router()).PreviewAsync(Request("Fix the flaky test", effort, autonomy: requestedAutonomy), CancellationToken.None);

        preview.Posture.ShouldNotBeNull();
        preview.Posture!.Autonomy.ShouldBe(expectedAutonomy);
        preview.Posture.NetworkOn.ShouldBe(expectedNetworkOn);
        preview.Posture.Network.ShouldStartWith(expectedNetworkPrefix);
    }

    [Fact]
    public async Task Posture_names_the_operators_own_ceiling_override_when_it_is_what_denies_network()
    {
        // The Coordination tab's tighten-only ceiling, not just the effort preset's own — the same second bound
        // TaskLaunchService.ClampAutonomy composes.
        var request = Request("Ship the migration", TaskEffortModes.Deep, autonomy: "Trusted", caps: new RouteCaps { AutonomyCeiling = "Standard" });

        var preview = await Preview(Router()).PreviewAsync(request, CancellationToken.None);

        preview.Posture!.Autonomy.ShouldBe("Standard", "the operator's own ceiling override tightens the deep preset's Trusted ceiling");
        preview.Posture.NetworkOn.ShouldBeFalse();
        preview.Posture.Network.ShouldStartWith("Network: clamped off by policy (ceiling Standard)");
    }

    [Theory]
    [InlineData(TaskEffortModes.Quick, null, CompletionEnforcementMode.Shadow)]    // single-agent: below the Enforceable bar, so the platform default (Shadow) stands
    [InlineData(TaskEffortModes.Deep, null, CompletionEnforcementMode.Enforced)]   // supervisor: Enforceable standing ⇒ Enforced BY DEFAULT
    [InlineData(TaskEffortModes.Deep, "shadow", CompletionEnforcementMode.Shadow)] // an explicit opt-in always wins, even where Enforced would otherwise default
    public async Task Posture_completion_mode_matches_the_runs_operating_mode_standing(string effort, string? completionMode, CompletionEnforcementMode expected)
    {
        var preview = await Preview(Router()).PreviewAsync(Request("Roll out the change", effort, completionMode: completionMode), CancellationToken.None);

        preview.Posture!.CompletionMode.ShouldBe(expected);
    }

    [Fact]
    public async Task Posture_refuses_an_enforced_opt_in_the_runs_mode_cannot_back_exactly_like_the_launch_would()
    {
        // Quick projects single-agent, which holds only Shadow standing — CompletionPolicy.StampModeFor throws
        // rather than silently downgrading, so a preview asking about this exact input fails exactly like a launch
        // of it would, instead of showing a posture the launch could never actually reach.
        var request = Request("Ship it unattended", TaskEffortModes.Quick, completionMode: "enforced");

        await Should.ThrowAsync<InvalidOperationException>(() => Preview(Router()).PreviewAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Preview_and_launch_derive_identical_posture_for_identical_inputs()
    {
        // The load-bearing claim behind the whole DTO: the preview's Posture is not a parallel computation that
        // HAPPENS to agree — it calls the SAME functions the launch calls. Pinned by calling both paths here, over
        // the SAME resolved route, rather than asserting against a hand-computed expected string.
        var router = Router();
        var request = Request("Wire the retry loop", TaskEffortModes.Deep, autonomy: "Trusted", caps: new RouteCaps { AutonomyCeiling = "Standard" }, completionMode: "shadow");

        var seed = await new ChatSeedProvider().SeedAsync(request, CancellationToken.None);
        var route = await router.RouteAsync(TaskLaunchService.BuildRouteRequest(seed, request), CancellationToken.None);

        var previewedPosture = (await Preview(router).PreviewAsync(request, CancellationToken.None)).Posture!;

        // The launch side: the SAME BuildAgentProfile clamp + the SAME StampModeFor call the run-staging path makes.
        var launchAutonomy = TaskLaunchService.BuildAgentProfile(request, seed, route).AutonomyLevel;
        var launchMode = RunModeClassifier.DeriveFromJson(route.ProjectionKind, definitionJson: null);
        var launchCompletionMode = CompletionPolicy.StampModeFor(request.CompletionMode, launchMode, new ModeProfileRegistry().Resolve(launchMode));

        previewedPosture.Autonomy.ShouldBe(launchAutonomy);
        previewedPosture.CompletionMode.ShouldBe(launchCompletionMode);
    }

    // Persistence has real HTTP/Postgres coverage; this unit fixture isolates seed/scope/router composition.
    private sealed class RoutingOnlySnapshotStore(IEffortRouter router) : ITaskRouteSnapshotService
    {
        public async Task<TaskRoutePreviewResult> CreateAsync(TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken) => new() { Route = await router.RouteAsync(TaskLaunchService.BuildRouteRequest(seed, request), cancellationToken), DeploymentAutonomyCeiling = "Unleashed" };
        public Task<TaskRouteSnapshotDecision> ReadAsync(TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LaunchTaskResult> ConsumeAsync(TaskRouteSnapshotConsumption consumption, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A guard that accepts every repo — tenancy itself is proven against real Postgres in the integration tier; these tests pin the routing, not the query.</summary>
    private sealed class AllRepositoriesInTeam : ILaunchRepositoryScopeGuard
    {
        public Task EnsureInTeamAsync(TaskLaunchSeed seed, TaskLaunchRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
