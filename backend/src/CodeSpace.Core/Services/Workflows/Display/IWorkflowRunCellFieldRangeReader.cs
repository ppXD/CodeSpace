using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Display;

public interface IWorkflowRunCellFieldRangeReader
{
    Task<WorkflowRunCellFieldRangePage?> ReadAsync(WorkflowRunCellFieldRangeReadRequest request, CancellationToken cancellationToken);
}

public sealed record WorkflowRunCellFieldRangeReadRequest
{
    public required Guid TeamId { get; init; }
    public required Guid RequestedRunId { get; init; }
    public required WorkflowRunViewScope Scope { get; init; }
    public required Guid SourceRunId { get; init; }
    public required string NodeId { get; init; }
    public required string IterationKey { get; init; }
    public required WorkflowRunCellRecordIdentity Records { get; init; }
    public required WorkflowRunCellFieldSection Section { get; init; }
    public string? Name { get; init; }
    public string? Cursor { get; init; }
    public required int LimitBytes { get; init; }
}
