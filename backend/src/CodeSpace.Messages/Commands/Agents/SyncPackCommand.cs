using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Re-pull a pack from its saved source (the store's Sync): re-clone the pack's URL at its saved ref, refresh
/// every already-imported artifact in place (handles kept), and return what changed plus the discovered-but-not-
/// imported artifacts as a preview to add. New artifacts are NOT auto-imported.
/// </summary>
public sealed record SyncPackCommand : ICommand<PackSyncResult>, IRequireTeamMembership
{
    public required Guid PackId { get; init; }
}
