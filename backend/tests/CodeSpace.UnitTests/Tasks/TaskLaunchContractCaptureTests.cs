using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Contracts;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

[Trait("Category", "Unit")]
public class TaskLaunchContractCaptureTests
{
    [Fact]
    public void Capture_preserves_requested_obligations_even_when_the_projection_context_has_dropped_them()
    {
        var request = Request();
        var context = Context(request);
        var contract = TaskLaunchContractSnapshot.Capture(request, context);

        contract.Version.ShouldBe(TaskLaunchContract.CurrentVersion);
        contract.TaskText.ShouldBe(request.TaskText);
        contract.Goal.ShouldBe(context.Seed.Goal);
        contract.SurfaceKind.ShouldBe(context.Seed.SurfaceKind);
        contract.AcceptanceChecks.ShouldBe(request.AcceptanceChecks);
        contract.AcceptanceCriteria.ShouldBe(request.AcceptanceCriteria);
        contract.DeliverySpec.ShouldBe(request.DeliverySpec);
        context.AcceptanceChecks.ShouldBeNull("the original request, not a lossy projected context, is the provenance source");
        contract.RequestedControls.ShouldNotBeNull();
        contract.RequestedControls.Autonomy.ShouldBe("Unleashed");
        contract.ResolvedRoute.ShouldNotBeNull();
        contract.ResolvedRoute.EffectiveAutonomy.ShouldBe("Standard");
        contract.ResolvedAgentProfile!.OutputReviewMode.ShouldBe(ReviewMode.Gate);
        contract.RequestedControls.Overrides!.OutputReviewMode.ShouldBe(ReviewMode.None);
    }

    [Fact]
    public void Every_current_service_control_has_an_explicit_provenance_mapping()
    {
        var request = Request();
        var contract = TaskLaunchContractSnapshot.Capture(request, Context(request));
        var mappings = new Dictionary<string, string>
        {
            [nameof(TaskLaunchRequest.RepositoryId)] = nameof(TaskLaunchControls.RepositoryId),
            [nameof(TaskLaunchRequest.RelatedRepositories)] = nameof(TaskLaunchControls.RelatedRepositories),
            [nameof(TaskLaunchRequest.BaseBranch)] = nameof(TaskLaunchControls.BaseBranch),
            [nameof(TaskLaunchRequest.ContinueSessionId)] = nameof(TaskLaunchControls.ContinueSessionId),
            [nameof(TaskLaunchRequest.CompletionMode)] = nameof(TaskLaunchControls.CompletionMode),
            [nameof(TaskLaunchRequest.RequestedEffort)] = nameof(TaskLaunchControls.Effort),
            [nameof(TaskLaunchRequest.RequestedRecipe)] = nameof(TaskLaunchControls.Recipe),
            [nameof(TaskLaunchRequest.DeliverableShape)] = nameof(TaskLaunchControls.DeliverableShape),
            [nameof(TaskLaunchRequest.Autonomy)] = nameof(TaskLaunchControls.Autonomy),
            [nameof(TaskLaunchRequest.Overrides)] = nameof(TaskLaunchControls.Overrides),
            [nameof(TaskLaunchRequest.CapsOverride)] = nameof(TaskLaunchControls.CapsOverride),
            [nameof(TaskLaunchRequest.AllowedModelIds)] = nameof(TaskLaunchControls.AllowedModelIds),
            [nameof(TaskLaunchRequest.AllowedAgentDefinitionIds)] = nameof(TaskLaunchControls.AllowedAgentDefinitionIds),
            [nameof(TaskLaunchRequest.RequirePlanConfirmation)] = nameof(TaskLaunchControls.RequirePlanConfirmation),
            [nameof(TaskLaunchRequest.PlannerReviewMode)] = nameof(TaskLaunchControls.PlannerReviewMode),
            [nameof(TaskLaunchRequest.DecisionReviewMode)] = nameof(TaskLaunchControls.DecisionReviewMode),
            [nameof(TaskLaunchRequest.ReviewerModelId)] = nameof(TaskLaunchControls.ReviewerModelId),
            [nameof(TaskLaunchRequest.Tier)] = nameof(TaskLaunchControls.QualityTier),
        };

        foreach (var (requested, recorded) in mappings)
        {
            var expected = typeof(TaskLaunchRequest).GetProperty(requested)!.GetValue(request);
            var actual = typeof(TaskLaunchControls).GetProperty(recorded)!.GetValue(contract.RequestedControls);
            JsonSerializer.Serialize(actual, WorkflowJson.Options).ShouldBe(JsonSerializer.Serialize(expected, WorkflowJson.Options), requested);
        }

        var separatelyRecorded = new[] { nameof(TaskLaunchRequest.TaskText), nameof(TaskLaunchRequest.SurfaceKind), nameof(TaskLaunchRequest.AcceptanceCriteria), nameof(TaskLaunchRequest.AcceptanceChecks), nameof(TaskLaunchRequest.DeliverySpec) };
        var deliberatelyExcluded = new[] { nameof(TaskLaunchRequest.TeamId), nameof(TaskLaunchRequest.ActorUserId), nameof(TaskLaunchRequest.SurfacePayload) };
        mappings.Keys.Concat(separatelyRecorded).Concat(deliberatelyExcluded).Order(StringComparer.Ordinal)
            .ShouldBe(typeof(TaskLaunchRequest).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal), "a new launch control needs an explicit recorded or deliberately excluded decision");
    }

    [Fact]
    public void Capture_detaches_nested_caller_owned_collections()
    {
        var checks = new List<string> { "sh", "verify.sh" };
        var modelId = Guid.NewGuid();
        var modelIds = new List<Guid> { modelId };
        var tools = new List<string> { "Read", "Grep" };
        var related = new List<TaskRelatedRepository> { new() { RepositoryId = Guid.NewGuid(), Alias = "api", Access = "read" } };
        var request = Request() with { AcceptanceChecks = checks, AllowedModelIds = modelIds, RelatedRepositories = related, Overrides = new TaskExecutionOverrides { AllowedTools = tools } };
        var context = Context(request) with { AgentProfile = new ResolvedAgentProfile { AllowedTools = tools, AutonomyLevel = "Standard" } };
        var contract = TaskLaunchContractSnapshot.Capture(request, context);

        checks[1] = "always-pass.sh";
        modelIds.Clear();
        tools.Add("Bash");
        related.Clear();

        contract.AcceptanceChecks.ShouldBe(new[] { "sh", "verify.sh" });
        contract.RequestedControls!.AllowedModelIds.ShouldBe(new[] { modelId });
        contract.RequestedControls.Overrides!.AllowedTools.ShouldBe(new[] { "Read", "Grep" });
        contract.RequestedControls.RelatedRepositories!.Single().Alias.ShouldBe("api");
        contract.ResolvedAgentProfile!.AllowedTools.ShouldBe(new[] { "Read", "Grep" });
    }

    [Fact]
    public void Absent_controls_stay_absent_and_surface_payload_is_not_persisted()
    {
        var request = new TaskLaunchRequest
        {
            TeamId = Guid.NewGuid(), ActorUserId = Guid.NewGuid(), SurfaceKind = "issue",
            SurfacePayload = new Dictionary<string, JsonElement> { ["accessToken"] = JsonSerializer.SerializeToElement("surface-secret-not-for-provenance") },
        };
        var contract = TaskLaunchContractSnapshot.Capture(request, Context(request));
        var controls = contract.RequestedControls!;

        contract.TaskText.ShouldBeNull("an issue-derived goal is not a fabricated verbatim user message");
        contract.AcceptanceChecks.ShouldBeNull();
        controls.AllowedModelIds.ShouldBeNull();
        controls.RequirePlanConfirmation.ShouldBeNull();
        controls.CapsOverride.ShouldBeNull();
        controls.QualityTier.ShouldBeNull();
        controls.PlannerReviewMode.ShouldBe(ReviewMode.None, "this records the service DTO default, not proof of an explicit user choice");
        var json = JsonSerializer.Serialize(contract, WorkflowJson.Options);
        json.ShouldNotContain("surface-secret-not-for-provenance");
        json.ShouldNotContain("authorized");
        json.ShouldNotContain("verified");
    }

    private static TaskLaunchRequest Request() => new()
    {
        TeamId = Guid.NewGuid(), ActorUserId = Guid.NewGuid(), SurfaceKind = "chat", TaskText = "  Original user goal  ",
        RepositoryId = Guid.NewGuid(), RelatedRepositories = [new() { RepositoryId = Guid.NewGuid(), Alias = "ui", Access = "read" }],
        BaseBranch = "work", ContinueSessionId = Guid.NewGuid(), CompletionMode = "enforced", RequestedEffort = "auto", RequestedRecipe = "custom-recipe",
        DeliverableShape = "document", Autonomy = "Unleashed", AcceptanceCriteria = ["Use the original data", "Retain the limitations"], AcceptanceChecks = ["sh", "verify.sh"],
        DeliverySpec = new() { OpenPullRequest = false, TargetBranch = "review" }, CapsOverride = new() { MaxCostUsd = 3.25m, MaxParallelism = 2, AutonomyCeiling = "Standard" },
        AllowedModelIds = [Guid.NewGuid()], AllowedAgentDefinitionIds = [], RequirePlanConfirmation = false,
        PlannerReviewMode = ReviewMode.Improve, DecisionReviewMode = ReviewMode.Gate, ReviewerModelId = Guid.NewGuid(), Tier = QualityTier.Delivery,
        Overrides = new() { Harness = "codex-cli", Model = "opaque-model", ModelCredentialId = Guid.NewGuid(), AllowedTools = ["Read"], PushBranch = false, EnableMcp = false, TimeoutSeconds = 0, OutputReviewMode = ReviewMode.None },
    };

    private static TaskBuildContext Context(TaskLaunchRequest request) => new()
    {
        Seed = new() { TeamId = request.TeamId, SurfaceKind = request.SurfaceKind, Goal = "Normalized launch goal" },
        Route = new() { ProjectionKind = TaskProjectionKinds.PlanMapSynth, EffortMode = "standard", Caps = new() { MaxCostUsd = 2m, AutonomyCeiling = "Standard" } },
        AgentProfile = new() { AutonomyLevel = "Standard", OutputReviewMode = ReviewMode.Gate },
    };
}
