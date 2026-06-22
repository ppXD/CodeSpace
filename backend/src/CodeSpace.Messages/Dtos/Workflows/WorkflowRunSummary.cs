using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// History-row shape. <see cref="SourceType"/> is an open string sourced from
/// <c>workflow_run_request.source_type</c> via the run's back-pointer.
/// </summary>
public sealed record WorkflowRunSummary
{
    public required Guid Id { get; init; }

    /// <summary>Parent workflow id for an authored run. <c>null</c> for a snapshot run (it has no parent workflow).</summary>
    public Guid? WorkflowId { get; init; }

    /// <summary>Pinned version for an authored run. <c>null</c> for a snapshot run.</summary>
    public int? WorkflowVersion { get; init; }

    /// <summary>The parent workflow's display name (LEFT JOIN). <c>null</c> for a snapshot / task run (no parent workflow), so the index can label a row without a second lookup.</summary>
    public string? WorkflowName { get; init; }

    /// <summary>Open-string source identifier. Examples: "manual", "replay", "provider.github.pull_request".</summary>
    public required string SourceType { get; init; }

    public required WorkflowRunStatus Status { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
}
