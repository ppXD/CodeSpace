using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Instantiate a new WORKING (bindable) skill from a Library STORE snapshot — a content COPY, the skill analogue of
/// <see cref="InstantiateAgentFromStoreCommand"/>. The copy is <c>Origin=Imported</c>, <c>Scope=Working</c>, carries
/// <c>source_definition_id</c> / <c>source_version</c> provenance (no <c>pack_id</c>, so it doesn't re-appear in the
/// Library), and gets a team-unique handle auto-suffixed only when a bench skill already owns it. This is how binding
/// a Library skill to an agent works: instantiate a working copy, then bind it. The snapshot itself is untouched.
/// </summary>
public sealed record InstantiateSkillFromStoreCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required Guid SourceDefinitionId { get; init; }
}
