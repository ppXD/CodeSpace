using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Display;

public interface IWorkflowRunCellFieldReader
{
    Task<WorkflowRunCellFieldPage?> ReadAsync(WorkflowRunCellFieldReadRequest request, CancellationToken cancellationToken);
}

public sealed record WorkflowRunCellFieldReadRequest
{
    public required Guid TeamId { get; init; }
    public required Guid RequestedRunId { get; init; }
    public required WorkflowRunViewScope Scope { get; init; }
    public required Guid SourceRunId { get; init; }
    public required string NodeId { get; init; }
    public required string IterationKey { get; init; }
    public string? Cursor { get; init; }
    public required int Limit { get; init; }
}
