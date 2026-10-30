using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

public interface IExternalTriggerStore : IScopedDependency
{
    Task<ExternalTriggerView?> FindAsync(Guid triggerId, Guid teamId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ExternalTriggerView>> ListForWorkflowAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken);
    Task<ExternalTriggerView> EnsureForNodeAsync(Guid workflowId, Guid teamId, string nodeId, Guid actorId, CancellationToken cancellationToken);
    Task<string> RotateKeyAsync(Guid triggerId, Guid teamId, Guid actorId, CancellationToken cancellationToken);
}

public sealed record ExternalTriggerView(Guid Id, Guid TeamId, Guid WorkflowId, string NodeId);
