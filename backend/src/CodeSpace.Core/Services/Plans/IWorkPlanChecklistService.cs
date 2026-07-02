using CodeSpace.Messages.Plans;

namespace CodeSpace.Core.Services.Plans;

/// <summary>
/// Projects a run's CURRENT plan into the live checklist read model (Rule 18.3 — the projection abstraction
/// beside the store): the persisted contract + per-item execution state DERIVED from the durable tape at
/// read time (never stored — one source of truth, replay-identical). Read-only.
/// </summary>
public interface IWorkPlanChecklistService
{
    /// <summary>The run's current-plan checklist, or null when the run has no plan (or is foreign). Team-scoped.</summary>
    Task<WorkPlanChecklist?> GetCurrentAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);
}
