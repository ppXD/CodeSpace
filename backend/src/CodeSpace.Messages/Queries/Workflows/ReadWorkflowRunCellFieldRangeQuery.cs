using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>Read one bounded UTF-8-safe window of one exact field returned by the descriptor endpoint.</summary>
public sealed record ReadWorkflowRunCellFieldRangeQuery : IQuery<WorkflowRunCellFieldRangePage?>, IRequireTeamMembership
{
    public const int DefaultPageBytes = 64 * 1024;
    public const int MaximumPageBytes = 64 * 1024;

    public required Guid RunId { get; init; }
    public WorkflowRunViewScope Scope { get; init; } = WorkflowRunViewScope.LineageMerged;
    public required Guid SourceRunId { get; init; }
    public required string NodeId { get; init; }
    public string? IterationKey { get; init; }
    public required Guid StateRecordId { get; init; }
    public required long StateRecordSequence { get; init; }
    public Guid? FirstStartedRecordId { get; init; }
    public long? FirstStartedRecordSequence { get; init; }
    public required WorkflowRunCellFieldSection Section { get; init; }
    public string? Name { get; init; }
    public string? Cursor { get; init; }
    public int LimitBytes { get; init; } = DefaultPageBytes;
}
