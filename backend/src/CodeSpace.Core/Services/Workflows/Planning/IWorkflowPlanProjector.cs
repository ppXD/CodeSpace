using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Dtos.Workflows.Planning;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// Deterministically maps a <see cref="PlannedWorkflow"/> onto a FIXED, safe workflow skeleton — the
/// model's data becomes node CONFIG, never node identity or wiring. The concern's projection abstraction
/// at the Planning root (Rule 18.3).
///
/// <para>Contract: <see cref="Project"/> ALWAYS emits a definition that passes <c>DefinitionValidator</c>.
/// A failure to validate is a bug in the projector, not in the input — the planning service runs every
/// projection through the validator before returning, and a unit test pins that a representative plan
/// projects to a valid definition.</para>
/// </summary>
public interface IWorkflowPlanProjector
{
    WorkflowDefinition Project(PlannedWorkflow plan);
}
