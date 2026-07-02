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

    /// <summary>
    /// Lineage key for this entry — the <c>RootRunId ?? Id</c> the team index collapses on. A row in the index is
    /// always the LATEST attempt of its lineage; <see cref="AttemptCount"/> counts how many runs share it.
    /// </summary>
    public required Guid RootRunId { get; init; }

    /// <summary>How many runs share this lineage root (1 = a never-rerun run). Drives the "N attempts" chip.</summary>
    public required int AttemptCount { get; init; }

    /// <summary>
    /// Whether the run belongs to a work session (its <c>WorkflowRun.SessionId</c> is set). The index opens a
    /// session-backed run as the full-page Session room and a session-less run as the raw-detail modal over the list.
    /// </summary>
    public required bool HasSession { get; init; }

    /// <summary>
    /// The lineage ROOT's source type — equal to <see cref="SourceType"/> for a never-rerun run, but the ORIGINAL's
    /// source for a rerun representative (whose own <see cref="SourceType"/> is "replay"/"rerun"). The row shows the
    /// root's identity, so a task lineage's title reads as the original task, not "Replay".
    /// </summary>
    public required string RootSourceType { get; init; }
}
