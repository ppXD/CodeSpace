using CodeSpace.Messages.Dtos.Workflows.ModelCalls;

namespace CodeSpace.Core.Services.Workflows.ModelCalls;

public interface IWorkflowRunModelCallReader
{
    Task<WorkflowRunModelCallDetailMetadata?> ReadByIdAsync(Guid runId, Guid modelCallId, Guid teamId, CancellationToken cancellationToken);
    Task<WorkflowRunModelCallBodyPage?> ReadBodyAsync(WorkflowRunModelCallBodyReadRequest request, CancellationToken cancellationToken);
    Task<WorkflowRunModelCallMetadata?> ReadMetadataAsync(Guid runId, long sequence, Guid teamId, CancellationToken cancellationToken);
    Task<WorkflowRunModelCallPartPage?> ReadPartAsync(WorkflowRunModelCallPartReadRequest request, CancellationToken cancellationToken);
}

public sealed record WorkflowRunModelCallBodyReadRequest(Guid RunId, Guid ModelCallId, Guid TeamId, WorkflowRunModelCallBody Body)
{
    public Guid? AttemptId { get; init; }
    public long OffsetBytes { get; init; }
    public int LimitBytes { get; init; } = WorkflowRunModelCallReader.DefaultPageBytes;
}

public sealed record WorkflowRunModelCallPartReadRequest(Guid RunId, long Sequence, Guid TeamId, WorkflowRunModelCallPart Part)
{
    public long OffsetBytes { get; init; }
    public int LimitBytes { get; init; } = WorkflowRunModelCallReader.DefaultPageBytes;
}
