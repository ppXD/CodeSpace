using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Add the selected artifacts a Sync discovered to the pack they were discovered in: re-clone the pack's SAVED source
/// at its saved ref (the clone uses the pack's sealed credential when it has one; the request carries no URL) and
/// upsert exactly the chosen <see cref="SourcePaths"/> into THAT pack. Resolving the pack again by URL would be wrong
/// for a private pack, whose stored URL no longer carries the credential it needs. Returns a per-path outcome.
/// </summary>
public sealed record ImportPackArtifactsCommand : ICommand<PackImportResult>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.AgentsWrite;

    /// <summary>The pack to import into. Not <c>required</c>: the body carries only the selection, and the controller sets this from the route.</summary>
    public Guid PackId { get; init; }

    /// <summary>The SourcePaths the operator selected from the sync's new artifacts, agents or skills.</summary>
    public IReadOnlyList<string> SourcePaths { get; init; } = Array.Empty<string>();
}
