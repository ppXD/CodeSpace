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
/// </summary>
public sealed record ReapUnreferencedArtifactsCommand : ICommand<ReapUnreferencedArtifactsResponse>;

/// <summary>The sweep's per-bucket counts, surfaced for logging and for the recurring job's result.</summary>
public sealed record ReapUnreferencedArtifactsResponse
{
    public required ArtifactRetentionSweepSummary Summary { get; init; }
}
