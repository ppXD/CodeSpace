using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Display;

public interface IWorkflowRunViewMetadataReader
{
    Task<WorkflowRunViewMetadata?> ReadAsync(Guid runId, Guid teamId, WorkflowRunViewScope scope, CancellationToken cancellationToken);
}
