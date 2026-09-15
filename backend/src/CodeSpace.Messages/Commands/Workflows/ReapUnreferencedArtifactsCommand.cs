using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Run one bounded artifact-retention sweep: claim a batch of live retention declarations, establish whether each
/// declared artifact is still referenced anywhere, and collect only those proven unreferenced past their class's age
/// floor and quarantine window.
///
/// <para>NOT tenant-scoped — system-wide reclamation that runs without an actor context. Fired by the recurring
/// reaper job; also sendable ad-hoc from an admin path or a test.</para>
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each retention row is claimed by bumping its own fence
/// epoch and lease, and a purge that fails leaves only that row for the next pass. One command transaction around the
/// whole tick would let a single bad row undo every other row's work.</para>
/// </summary>
public sealed record ReapUnreferencedArtifactsCommand : ICommand<ReapUnreferencedArtifactsResponse>, INonTransactionalCommand;

/// <summary>The sweep's per-bucket counts, surfaced for logging and for the recurring job's result.</summary>
public sealed record ReapUnreferencedArtifactsResponse
{
    public required ArtifactRetentionSweepSummary Summary { get; init; }
}
