using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows.Planning;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// Orchestrates task-first planning: gate the flag → plan → project → validate → return. Owns the
/// feature flag (Rule 8) so the handler stays a thin dispatcher (Rule 16). Does NOT persist a workflow and
/// does NOT create a run — it returns a reviewable plan + a validated definition the operator saves+runs.
/// </summary>
public interface IWorkflowPlanningService
{
    Task<PlanWorkflowFromTaskResult> PlanFromTaskAsync(WorkflowPlanRequest request, CancellationToken cancellationToken);
}
