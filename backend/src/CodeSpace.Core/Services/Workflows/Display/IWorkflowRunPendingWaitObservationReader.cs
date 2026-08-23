using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Display;

/// <summary>Indexed single-row access to the latest pending wait and its bounded approver-facing prompt.</summary>
public interface IWorkflowRunPendingWaitObservationReader
{
    Task<WorkflowRunPendingWaitObservation?> ReadAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);
}
