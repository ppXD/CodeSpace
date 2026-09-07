using System.Text.Json;
using CodeSpace.Core.Handlers.CommandHandlers.Tasks;
using CodeSpace.Core.Services.Tasks.Bounds;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Quick;
using CodeSpace.Core.Services.Tasks.Capabilities;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Core.Services.Tasks.RoutePreview;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

[Trait("Category", "Unit")]
public sealed class TaskRouteSnapshotContractTests
{
    [Fact]
    public void A_legacy_preview_does_not_claim_a_recorded_reference_or_expiry()
    {
        var preview = JsonSerializer.Deserialize<TaskRoutePreviewResult>("""{"route":{"projectionKind":"single-agent"},"deploymentAutonomyCeiling":"Standard"}""", WorkflowJson.Options)!;
        preview.RouteSnapshotId.ShouldBeNull();
        preview.ExpiresAt.ShouldBeNull();
        preview.CreatedAt.ShouldBeNull();
        var written = JsonSerializer.SerializeToElement(preview, WorkflowJson.Options);
        written.TryGetProperty("routeSnapshotId", out _).ShouldBeFalse();
        written.TryGetProperty("expiresAt", out _).ShouldBeFalse();
        written.TryGetProperty("createdAt", out _).ShouldBeFalse();
    }

    [Fact]
    public void Preview_and_launch_map_the_same_complete_wire_input_including_review_and_acceptance_controls()
    {
        const string wire = """
        {"taskText":"Review the contract","effort":"auto","recipe":"single-agent","autonomy":"Confined","timeoutSeconds":90,"enableMcp":false,"pushBranch":false,"allowedTools":["Read"],"acceptanceCriteria":["cite the source"],"acceptanceChecks":["dotnet","test"],"requirePlanConfirmation":true,"plannerReviewMode":"Gate","decisionReviewMode":"Improve","outputReviewMode":"Gate","reviseRounds":2,"reviewerAgent":true,"tier":"Delivery","caps":{"maxParallelism":3,"maxCostUsd":1.5}}
        """;
        var team = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var preview = LaunchTaskCommandHandler.BuildRequest(JsonSerializer.Deserialize<PreviewTaskRouteCommand>(wire, WorkflowJson.Options)!, team, actor);
        var launch = LaunchTaskCommandHandler.BuildRequest(JsonSerializer.Deserialize<LaunchTaskCommand>(wire, WorkflowJson.Options)!, team, actor);
        TaskRouteSnapshotService.InputDigest(preview).ShouldBe(TaskRouteSnapshotService.InputDigest(launch));
        preview.AcceptanceCriteria.ShouldBe(["cite the source"]);
        preview.Overrides.EnableMcp.ShouldBe(false);
        preview.Overrides.PushBranch.ShouldBe(false);
        preview.Overrides.AllowedTools.ShouldBe(["Read"]);
        preview.RequestedRecipe.ShouldBe("single-agent");
        typeof(PreviewTaskRouteCommand).BaseType.ShouldBe(typeof(LaunchTaskCommand).BaseType, "future wire controls must be shared, not copied selectively");
    }

    [Fact]
    public void Only_the_transport_reference_is_excluded_from_the_input_digest()
    {
        var request = new TaskLaunchRequest { TeamId = Guid.NewGuid(), ActorUserId = Guid.NewGuid(), SurfaceKind = "chat", TaskText = "Explain this" };
        var digest = TaskRouteSnapshotService.InputDigest(request);
        TaskRouteSnapshotService.InputDigest(request with { RouteSnapshotId = Guid.NewGuid() }).ShouldBe(digest);
        foreach (var changed in new[]
        {
            request with { TeamId = Guid.NewGuid() }, request with { ActorUserId = Guid.NewGuid() },
            request with { AcceptanceChecks = ["test"] }, request with { AllowedModelIds = [Guid.NewGuid()] },
            request with { Overrides = new TaskExecutionOverrides { EnableMcp = false } },
            request with { SurfacePayload = new Dictionary<string, JsonElement> { ["source"] = JsonSerializer.SerializeToElement("updated") } },
        }) TaskRouteSnapshotService.InputDigest(changed).ShouldNotBe(digest);
    }

    [Fact]
    public void Policy_fingerprint_is_stable_but_changes_when_a_registered_capability_changes()
    {
        var probe = new MutableProbe();
        var policy = new TaskRoutePolicyFingerprint(new TaskRecipeRegistry([new SingleAgentRecipe()]), new BoundsPresetRegistry([new QuickBoundsPreset()]), new CapabilityProbeRegistry([probe]), new EffortClassifierRegistry([new HeuristicEffortClassifier()]));
        var first = policy.Capture();
        policy.Capture().ShouldBe(first);
        probe.Available = false;
        policy.Capture().ShouldNotBe(first, "runtime capability changes must invalidate a preview even on the same worker build");
    }

    private sealed class MutableProbe : ICapabilityProbe
    {
        public string Capability => "contract-test-capability";
        public bool Available { get; set; } = true;
        public bool IsAvailable() => Available;
    }
}
