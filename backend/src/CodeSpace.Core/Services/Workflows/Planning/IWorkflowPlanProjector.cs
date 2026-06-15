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

    /// <summary>
    /// Project a plan onto the L3 CHECKPOINT-COORDINATED variant: a <c>flow.loop</c> graph where a
    /// coordinator <c>llm.complete</c> re-decides BETWEEN rounds (plan → parallel work → coordinator judges →
    /// rework/done/abort across bounded rounds). Built entirely by composing existing nodes — no new engine
    /// code. Same always-valid contract as <see cref="Project"/>; <paramref name="options"/> folds the round
    /// cap + per-round parallelism into the generated loop/map config.
    /// </summary>
    WorkflowDefinition ProjectCoordinated(PlannedWorkflow plan, CoordinationOptions options);
}
