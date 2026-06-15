using CodeSpace.Messages.Dtos.Workflows.Planning;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// Turns a free-text task into a structured <see cref="PlannedWorkflow"/> — the platform's first
/// first-party planning step. The concern's IDENTITY abstraction (Rule 18.3), so it sits at the
/// Planning root; the interchangeable implementations live in <c>Planners/</c>.
///
/// <para>Deliberately ONE method (Rule 7 — narrow). The structured-LLM impl ships now
/// (<c>LlmWorkflowPlanner</c>); future siblings (an agent-driven planner, a template-matching planner)
/// slot in as new <c>IWorkflowPlanner</c> implementations WITHOUT touching this interface — selection
/// becomes a registry concern then, not an interface change. A new capability (e.g. streaming partial
/// plans) would arrive as a SIBLING interface, never by widening this one.</para>
/// </summary>
public interface IWorkflowPlanner
{
    Task<PlannedWorkflow> PlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken);
}
