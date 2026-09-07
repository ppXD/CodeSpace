using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Tasks;

/// <summary>
/// Operator-facing controls as received by the launch service, before routing, clamping, or projection.
/// Keep null, false, and empty distinct. These are recorded requests, not effective permissions or success facts.
/// Surface payload and credential secrets deliberately do not belong in this persisted record.
/// </summary>
public sealed record TaskLaunchControls
{
    public Guid? RepositoryId { get; init; }
    public IReadOnlyList<TaskRelatedRepository>? RelatedRepositories { get; init; }
    public string? BaseBranch { get; init; }
    public Guid? ContinueSessionId { get; init; }
    public string? CompletionMode { get; init; }
    public string? Effort { get; init; }
    public string? Recipe { get; init; }
    public string? DeliverableShape { get; init; }
    public string? Autonomy { get; init; }
    public TaskExecutionOverrides? Overrides { get; init; }
    public RouteCaps? CapsOverride { get; init; }
    public IReadOnlyList<Guid>? AllowedModelIds { get; init; }
    public IReadOnlyList<Guid>? AllowedAgentDefinitionIds { get; init; }
    public bool? RequirePlanConfirmation { get; init; }
    public ReviewMode? PlannerReviewMode { get; init; }
    public ReviewMode? DecisionReviewMode { get; init; }
    public Guid? ReviewerModelId { get; init; }
    public QualityTier? QualityTier { get; init; }
}
