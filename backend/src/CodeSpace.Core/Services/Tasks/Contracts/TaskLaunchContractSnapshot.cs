using System.Text.Json;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Contracts;

/// <summary>The producer and compatibility boundary for launch intent in frozen workflow snapshots.</summary>
public static class TaskLaunchContractSnapshot
{
    public static TaskLaunchContract Capture(TaskLaunchRequest request, TaskBuildContext context)
    {
        var contract = new TaskLaunchContract
        {
            Version = TaskLaunchContract.CurrentVersion,
            TaskText = request.TaskText,
            Goal = context.Seed.Goal,
            SurfaceKind = context.Seed.SurfaceKind,
            AcceptanceCriteria = request.AcceptanceCriteria,
            AcceptanceChecks = request.AcceptanceChecks,
            DeliverySpec = request.DeliverySpec,
            RequestedControls = new TaskLaunchControls
            {
                RepositoryId = request.RepositoryId,
                RelatedRepositories = request.RelatedRepositories,
                BaseBranch = request.BaseBranch,
                ContinueSessionId = request.ContinueSessionId,
                CompletionMode = request.CompletionMode,
                Effort = request.RequestedEffort,
                Recipe = request.RequestedRecipe,
                DeliverableShape = request.DeliverableShape,
                Autonomy = request.Autonomy,
                Overrides = request.Overrides,
                CapsOverride = request.CapsOverride,
                AllowedModelIds = request.AllowedModelIds,
                AllowedAgentDefinitionIds = request.AllowedAgentDefinitionIds,
                RequirePlanConfirmation = request.RequirePlanConfirmation,
                PlannerReviewMode = request.PlannerReviewMode,
                DecisionReviewMode = request.DecisionReviewMode,
                ReviewerModelId = request.ReviewerModelId,
                QualityTier = request.Tier,
            },
            ResolvedRoute = ResolvedRoute(context),
            ResolvedAgentProfile = context.AgentProfile,
            SupervisorModelSelection = context.SupervisorModelSelection,
            PlannerModelSelection = context.PlannerModelSelection,
        };

        // Detach nested caller-owned lists as well as the outer record before a projection sees the context.
        // The canonical persisted representation is also the copy boundary, so future additive nested fields
        // cannot accidentally retain mutable aliases to request/profile data.
        return JsonSerializer.SerializeToElement(contract, WorkflowJson.Options).Deserialize<TaskLaunchContract>(WorkflowJson.Options)!;
    }

    public static RoutePlan ResolvedRoute(TaskBuildContext context) =>
        context.AgentProfile?.AutonomyLevel is { Length: > 0 } tier ? context.Route with { EffectiveAutonomy = tier } : context.Route;

    /// <summary>Absent legacy provenance remains unknown; present unsupported or incomplete provenance must not be executed as an understood contract.</summary>
    public static IReadOnlyList<string> Validate(TaskLaunchContract? contract)
    {
        if (contract is null) return [];

        var errors = new List<string>();
        if (contract.Version != TaskLaunchContract.CurrentVersion) errors.Add($"Unsupported launchContract version {contract.Version}. Expected {TaskLaunchContract.CurrentVersion}.");
        if (string.IsNullOrWhiteSpace(contract.Goal)) errors.Add("launchContract.goal is required.");
        if (string.IsNullOrWhiteSpace(contract.SurfaceKind)) errors.Add("launchContract.surfaceKind is required.");
        if (contract.RequestedControls is null) errors.Add("launchContract.requestedControls is required.");
        if (string.IsNullOrWhiteSpace(contract.ResolvedRoute?.ProjectionKind)) errors.Add("launchContract.resolvedRoute.projectionKind is required.");
        ValidateSelection(contract.SupervisorModelSelection, "supervisorModelSelection", errors);
        ValidateSelection(contract.PlannerModelSelection, "plannerModelSelection", errors);
        if (contract.SupervisorModelSelection is not null && contract.PlannerModelSelection is not null) errors.Add("launchContract cannot contain both supervisorModelSelection and plannerModelSelection.");
        return errors;
    }

    private static void ValidateSelection(ModelSelectionReceipt? receipt, string path, List<string> errors)
    {
        if (receipt is null) return;
        if (receipt.PolicyVersion != ModelSelectionReceipt.CurrentPolicyVersion) errors.Add($"Unsupported {path}.policyVersion '{receipt.PolicyVersion}'.");
        if (!Enum.IsDefined(receipt.Source)) errors.Add($"Unsupported {path}.source '{receipt.Source}'.");
        if (receipt.ModelCredentialModelId == Guid.Empty) errors.Add($"{path}.modelCredentialModelId is required.");
        if (string.IsNullOrWhiteSpace(receipt.Mode)) errors.Add($"{path}.mode is required.");
        if (string.IsNullOrWhiteSpace(receipt.CapabilityKey)) errors.Add($"{path}.capabilityKey is required.");
        if (receipt.Source != ModelSelectionSource.QualificationEvidence) return;

        if (receipt.QualificationReceiptId is null) errors.Add($"{path}.qualificationReceiptId is required for qualification evidence.");
        if (string.IsNullOrWhiteSpace(receipt.SuiteDigest)) errors.Add($"{path}.suiteDigest is required for qualification evidence.");
        if (string.IsNullOrWhiteSpace(receipt.EvidenceVersion)) errors.Add($"{path}.evidenceVersion is required for qualification evidence.");
        if (receipt.SampleSize is null or <= 0) errors.Add($"{path}.sampleSize must be positive for qualification evidence.");
        if (receipt.SolveRateLowerBound is null or < 0 or > 1) errors.Add($"{path}.solveRateLowerBound must be between zero and one.");
        if (receipt.AgeAdjustedScore is null or < 0 or > 1) errors.Add($"{path}.ageAdjustedScore must be between zero and one.");
        if (receipt.EvaluatorHealth is null or < 0 or > 1) errors.Add($"{path}.evaluatorHealth must be between zero and one.");
        if (receipt.EvidenceEffectiveFrom is null || receipt.EvidenceExpiresAt is null || receipt.EvidenceExpiresAt <= receipt.EvidenceEffectiveFrom) errors.Add($"{path} requires a valid evidence window.");
    }
}
