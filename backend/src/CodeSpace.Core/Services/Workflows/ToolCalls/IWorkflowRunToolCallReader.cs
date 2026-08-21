using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Workflows.ToolCalls;

namespace CodeSpace.Core.Services.Workflows.ToolCalls;

public interface IWorkflowRunToolCallReader : IScopedDependency
{
    Task<WorkflowRunToolCallPage?> ReadPageAsync(WorkflowRunToolCallPageRequest request, CancellationToken cancellationToken);
    Task<WorkflowRunToolCallDetail?> ReadDetailAsync(WorkflowRunToolCallDetailRequest request, CancellationToken cancellationToken);
}

public sealed record WorkflowRunToolCallPageRequest(Guid TeamId, Guid RunId, string? Cursor, int Limit);

public sealed record WorkflowRunToolCallDetailRequest(Guid TeamId, Guid RunId, Guid ToolCallId);
