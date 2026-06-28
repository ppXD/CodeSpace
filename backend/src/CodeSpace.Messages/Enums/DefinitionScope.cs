namespace CodeSpace.Messages.Enums;

/// <summary>
/// Whether an <c>AgentDefinition</c> / <c>SkillDefinition</c> is a live WORKING unit — on the team's bench,
/// @-mentionable, runnable — or a STORE snapshot: a Library item imported from a pack, instantiated into working
/// copies and never run directly. <see cref="Working"/> is the first/default member so any insert that forgets
/// to stamp the scope lands on the bench, never as an invisible store row. Orthogonal to the Origin enum: a
/// from-store copy is <c>Origin=Imported</c> AND <c>Scope=Working</c>; a snapshot is <c>Origin=Imported</c> AND
/// <c>Scope=Store</c>.
/// </summary>
public enum DefinitionScope
{
    Working,
    Store,
}
