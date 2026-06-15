using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection;

/// <summary>
/// Projects a <see cref="TaskBuildContext"/> into a <see cref="WorkflowDefinition"/> for one projection
/// strategy — the polymorphic seam the task layer fans out over. Each implementation owns ONE
/// <see cref="ProjectionKind"/> (an open string, e.g. <c>"single-agent"</c>) and self-registers via the
/// <c>ISingletonDependency</c> marker, so a new strategy is a new impl folder under
/// <c>Projection/Builders/&lt;Kind&gt;/</c> with ZERO edit to the registry / factory / core (Rule 18.3 +
/// Rule 7 — new strategies are sibling impls, never a wider interface).
///
/// <para>The build is a pure function of its context (Rule 16 — no DB, no router): same context → same
/// definition. The output MUST pass <c>DefinitionValidator</c> — the same always-valid contract
/// <c>IWorkflowPlanProjector.Project</c> holds, so the snapshot starter can run it unconditionally.</para>
/// </summary>
public interface IWorkflowDefinitionBuilder
{
    /// <summary>The projection kind this builder handles — the open string the registry indexes + resolves it by. Mirrors <c>IAgentHarness.Kind</c> / <c>ISandboxRunner.Kind</c>.</summary>
    string ProjectionKind { get; }

    /// <summary>Build the (always-valid) workflow definition for <paramref name="context"/>. Throws nothing for a well-formed context; the output is guaranteed to pass <c>DefinitionValidator</c>.</summary>
    WorkflowDefinition Build(TaskBuildContext context);
}
