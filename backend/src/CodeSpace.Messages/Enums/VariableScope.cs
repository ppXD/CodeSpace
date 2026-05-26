namespace CodeSpace.Messages.Enums;

/// <summary>
/// Discriminator for the owner of a <c>variable</c> row. The polymorphic <c>scope_id</c>
/// column on the table is interpreted against this enum:
///   • <see cref="Team"/>     → scope_id is a <c>team.id</c>
///   • <see cref="Workflow"/> → scope_id is a <c>workflow.id</c>
///
/// Adding a new scope (Project, ChatflowSession, Organization, ...) is additive: append a
/// new enum value, teach the engine to call <c>IVariableService.GetAllForEngineAsync</c>
/// for it, and update the editor's autocomplete. No DB schema change is required.
/// </summary>
public enum VariableScope
{
    Team = 0,
    Workflow = 1,
}
