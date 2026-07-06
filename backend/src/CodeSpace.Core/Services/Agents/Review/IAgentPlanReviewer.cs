using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Agents.Review;

/// <summary>
/// Reviews an AUTHORED PLAN with a REAL, INDEPENDENT agent (D① — grounded plan review): a read-only agent run that
/// CLONES the plan's target repository at its base state and verifies the plan against the actual code — do its
/// assumptions hold (files/frameworks/tests it presumes), are its steps feasible, necessary, and complete, is any of
/// it already done? The in-process plan critic judges plan TEXT; this judges the plan against REALITY. A sibling of
/// <see cref="IAgentOutputReviewer"/> (Rule 7); callers ladder agent → model critic → fail-open, so a grounded review
/// is never worse than a text review.
/// </summary>
public interface IAgentPlanReviewer
{
    /// <summary>Run one grounded review of <paramref name="planArtifact"/> (the rendered plan) against the repository. NEVER throws (cancellation aside) — failures return <c>CriticVerdict.ReviewFailed</c> for the ladder.</summary>
    Task<CriticVerdict> ReviewAsync(PlanReviewRequest request, CancellationToken cancellationToken);
}

/// <summary>One grounded plan-review request — the rendered plan, its goal, the repo to verify against, and the reviewer run's linkage.</summary>
public sealed record PlanReviewRequest
{
    /// <summary>The rendered plan under review (goal + subtasks + criteria + risks — the same rendering the model critic judges).</summary>
    public required string PlanArtifact { get; init; }

    /// <summary>The task the plan should serve — the reviewer's yardstick.</summary>
    public required string Goal { get; init; }

    /// <summary>The repository the plan targets — cloned read-only at its base state.</summary>
    public required Guid RepositoryId { get; init; }

    public required Guid TeamId { get; init; }

    /// <summary>Observability linkage for the reviewer run (the plan node's cell). Null on the run-less plan-from-task path.</summary>
    public Guid? WorkflowRunId { get; init; }

    public string? NodeId { get; init; }

    /// <summary>The operator's reviewer model pin; null ⇒ auto.</summary>
    public Guid? ReviewerModelId { get; init; }
}
