using CodeSpace.Messages.Dtos.Workflows.ModelCalls;

namespace CodeSpace.Core.Services.Workflows.ModelCalls;

public interface IWorkflowRunModelCallReader
{
    Task<WorkflowRunModelCallMetadata?> ReadMetadataAsync(Guid runId, long sequence, Guid teamId, CancellationToken cancellationToken);
    Task<WorkflowRunModelCallPartPage?> ReadPartAsync(WorkflowRunModelCallPartReadRequest request, CancellationToken cancellationToken);
}

public sealed record WorkflowRunModelCallPartReadRequest(Guid RunId, long Sequence, Guid TeamId, WorkflowRunModelCallPart Part)
{
    public long OffsetBytes { get; init; }
    public int LimitBytes { get; init; } = WorkflowRunModelCallReader.DefaultPageBytes;
}
