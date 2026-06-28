using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Instantiate a NEW working bench persona from a Library STORE snapshot — a content COPY, not a link to
/// the snapshot. The copy is <c>Origin=Imported</c>, <c>Scope=Working</c> (so it lands on the bench and is
/// runnable), carries <c>source_definition_id</c> / <c>source_version</c> provenance (the basis for a future
/// per-copy sync), starts UNBOUND, and gets a team-unique handle — the snapshot's derived handle, auto-suffixed
/// (<c>-2</c>, <c>-3</c>…) only when a bench persona already owns it, so picking a library item never dead-ends
/// (the editor opens after for renaming). The snapshot itself is untouched, so it stays in the Library.
/// </summary>
public sealed record InstantiateAgentFromStoreCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required Guid SourceDefinitionId { get; init; }
}
